using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ScreenCord.Contracts;

namespace ScreenCord.Api.Observability;

/// <summary>
/// Observabilidade sem serviços externos: métricas das requisições em memória, erros da API e do aplicativo
/// no SQLite e o painel em <c>/admin</c>.
/// </summary>
public static class ObservabilitySetup
{
    public const string TelemetryRateLimitPolicy = "telemetry";

    // O ASP.NET Core só cria o Activity da requisição (e lê o traceparent do aplicativo) se alguém escuta.
    private static readonly ActivityListener RequestListener = new()
    {
        ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    };

    static ObservabilitySetup() => ActivitySource.AddActivityListener(RequestListener);

    public static IServiceCollection AddScreenCordObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ObservabilityOptions>(configuration.GetSection(ObservabilityOptions.SectionName));
        services.AddSingleton<RequestMetrics>();
        services.AddSingleton<ErrorSink>();
        services.AddHostedService(sp => sp.GetRequiredService<ErrorSink>());
        services.AddSingleton<ILoggerProvider, ErrorLogProvider>();
        return services;
    }

    /// <summary>Relatórios de erro do aplicativo chegam sem login: limite mais apertado, por IP.</summary>
    public static void AddTelemetryRateLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options, Func<HttpContext, string> clientIp) =>
        options.AddPolicy(TelemetryRateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                clientIp(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));

    /// <summary>
    /// Mede cada requisição, devolve o trace em <see cref="TelemetryContract.TraceIdHeader"/> e manda para o painel
    /// as que terminam em 5xx, com a rota, a duração e a versão do aplicativo que fez a chamada.
    /// </summary>
    public static IApplicationBuilder UseRequestMetrics(this IApplicationBuilder app)
    {
        var metrics = app.ApplicationServices.GetRequiredService<RequestMetrics>();
        var sink = app.ApplicationServices.GetRequiredService<ErrorSink>();
        var time = app.ApplicationServices.GetRequiredService<TimeProvider>();

        return app.Use(async (context, next) =>
        {
            var traceId = Activity.Current?.TraceId.ToHexString();
            if (traceId is not null)
            {
                context.Response.Headers[TelemetryContract.TraceIdHeader] = traceId;
            }

            var webSocket = context.WebSockets.IsWebSocketRequest;
            var hub = context.Request.Path.StartsWithSegments(GroupsHubContract.Path);
            if (webSocket)
            {
                metrics.ConnectionOpened(hub);
            }

            var started = Stopwatch.GetTimestamp();
            var status = StatusCodes.Status500InternalServerError;
            try
            {
                await next(context);
                status = context.Response.StatusCode;
            }
            catch (BadHttpRequestException ex)
            {
                // Corpo inválido: no Development vira exceção, mas a resposta é 4xx.
                status = ex.StatusCode;
                throw;
            }
            finally
            {
                if (webSocket)
                {
                    metrics.ConnectionClosed(hub);
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var route = context.GetEndpoint() is RouteEndpoint endpoint
                    // Rotas de MapGroup vêm como "/groups/": sem a barra final, ficam como o aplicativo chama.
                    ? $"{context.Request.Method} /{endpoint.RoutePattern.RawText?.Trim('/')}"
                    : $"{context.Request.Method} (sem rota)";
                metrics.Record(route, status, elapsed, webSocket);

                if (status >= 500)
                {
                    var now = time.GetUtcNow();
                    var version = context.Request.Headers[ClientVersionContract.Header].ToString();
                    sink.Enqueue(new ErrorEvent
                    {
                        Id = Guid.NewGuid(),
                        Source = ErrorEvent.FromApi,
                        OccurredAtUtc = now,
                        ReceivedAtUtc = now,
                        Level = "Error",
                        Category = "http",
                        Message = $"{route} respondeu {status} em {elapsed:0} ms.",
                        TraceId = traceId,
                        Fingerprint = ErrorFingerprint.Compute(ErrorEvent.FromApi, "http", $"{route} {status}", null),
                        AppVersion = version.Length is > 0 and <= 32 ? version : null,
                        UserId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null,
                        Request = ErrorFingerprint.Truncate($"{context.Request.Method} {context.Request.Path} → {status} ({elapsed:0} ms)", 300),
                    });
                }
            }
        });
    }
}
