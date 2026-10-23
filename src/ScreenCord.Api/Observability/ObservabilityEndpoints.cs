using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenCord.Api.Data;
using ScreenCord.Api.Endpoints;
using ScreenCord.Api.Versioning;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Observability;

public static class ObservabilityEndpoints
{
    public const string DashboardPath = "/admin";

    /// <summary>Cabeçalho com o token do painel (<see cref="ObservabilityOptions.DashboardToken"/>).</summary>
    public const string TokenHeader = "X-Admin-Token";

    private static readonly string ApiVersion =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "desconhecida";

    private static readonly string[] KnownLevels = ["Warning", "Error", "Critical"];

    private static readonly Lazy<string> DashboardHtml = new(() =>
    {
        using var stream = typeof(ObservabilityEndpoints).Assembly.GetManifestResourceStream("ScreenCord.Api.Observability.dashboard.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static void MapObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(TelemetryContract.ErrorsPath, ReceiveErrorsAsync)
            .RequireRateLimiting(ObservabilitySetup.TelemetryRateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(512 * 1024))
            .WithTags("Infra")
            .WithSummary("Relatório de erros do aplicativo")
            .WithDescription($"Aceita sem login. Até {TelemetryContract.MaxEntries} entradas por relatório; textos longos são cortados.")
            .Produces(StatusCodes.Status202Accepted);

        // O painel em si é público (só HTML); os dados exigem o token. Sem token configurado, nada existe.
        app.MapGet(DashboardPath, (IOptions<ObservabilityOptions> options) =>
                string.IsNullOrEmpty(options.Value.DashboardToken)
                    ? Results.NotFound()
                    : Results.Content(DashboardHtml.Value, "text/html; charset=utf-8"))
            .ExcludeFromDescription();

        var api = app.MapGroup(DashboardPath + "/api").ExcludeFromDescription().AddEndpointFilter(async (context, next) =>
        {
            var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<ObservabilityOptions>>().Value.DashboardToken;
            if (string.IsNullOrEmpty(expected))
            {
                return Results.NotFound();
            }

            var given = context.HttpContext.Request.Headers[TokenHeader].ToString();
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)))
                ? await next(context)
                : ApiResults.Error(StatusCodes.Status401Unauthorized, "invalid_admin_token", "Token do painel inválido.");
        });

        api.MapGet("/summary", SummaryAsync);
        api.MapGet("/issues", IssuesAsync);
        api.MapGet("/events", EventsAsync);
        api.MapLimitsAdminEndpoints();
    }

    private static async Task<IResult> ReceiveErrorsAsync(ClientErrorReport? report, HttpContext context, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (report?.Entries is null || report.Entries.Count == 0 || string.IsNullOrWhiteSpace(report.AppVersion) ||
            !ClientVersionGate.TryParse(report.AppVersion, out _, out _))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_report", "Relatório de erros inválido.");
        }

        var now = time.GetUtcNow();
        var device = Details(report.Device);
        var userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        foreach (var entry in report.Entries.Where(e => e is not null).Take(TelemetryContract.MaxEntries))
        {
            var category = ErrorFingerprint.Truncate(string.IsNullOrWhiteSpace(entry.Category) ? "?" : entry.Category.Trim(), 200)!;
            var message = ErrorFingerprint.Truncate(entry.Message ?? "", ErrorEvent.MaxMessageLength)!;
            var exception = ErrorFingerprint.Truncate(entry.Exception, ErrorEvent.MaxExceptionLength);
            db.ErrorEvents.Add(new ErrorEvent
            {
                Id = Guid.NewGuid(),
                Source = ErrorEvent.FromApp,
                // Relógio do computador pode estar errado: fora de uma faixa razoável, vale a hora da chegada.
                OccurredAtUtc = entry.TimestampUtc > now.AddDays(-7) && entry.TimestampUtc < now.AddMinutes(5) ? entry.TimestampUtc : now,
                ReceivedAtUtc = now,
                Level = KnownLevels.FirstOrDefault(l => string.Equals(l, entry.Level, StringComparison.OrdinalIgnoreCase)) ?? "Error",
                Category = category,
                Message = message,
                Exception = exception,
                TraceId = entry.TraceId is { Length: 32 } trace && trace.All(char.IsAsciiHexDigitLower) ? trace : null,
                Fingerprint = ErrorFingerprint.Compute(ErrorEvent.FromApp, category, message, exception),
                AppVersion = ErrorFingerprint.Truncate(report.AppVersion.Trim(), 31),
                Os = ErrorFingerprint.Truncate(report.Os, 99),
                Environment = ErrorFingerprint.Truncate(report.Environment, 31),
                InstallId = report.InstallId == Guid.Empty ? null : report.InstallId,
                RunId = report.RunId == Guid.Empty ? null : report.RunId,
                UserId = userId,
                Device = device,
                Context = Details(entry.Context),
                Breadcrumbs = Breadcrumbs(entry.Breadcrumbs),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted();
    }

    /// <summary>Pares chave/valor do aplicativo em JSON, com limite de quantidade e de tamanho.</summary>
    private static string? Details(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values.Where(p => !string.IsNullOrWhiteSpace(p.Key)).Take(TelemetryContract.MaxContextEntries))
        {
            kept[ErrorFingerprint.Truncate(key.Trim(), 60)!] = ErrorFingerprint.Truncate(value ?? "", 300)!;
        }

        var json = JsonSerializer.Serialize(kept);
        return json.Length <= ErrorEvent.MaxDetailsLength ? json : null;
    }

    private static string? Breadcrumbs(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        // Fica com as mais novas, que são as que levaram ao erro.
        var kept = lines.Where(l => !string.IsNullOrEmpty(l)).TakeLast(TelemetryContract.MaxBreadcrumbs)
            .Select(l => ErrorFingerprint.Truncate(l.ReplaceLineEndings(" "), 280)!);
        var text = string.Join('\n', kept);
        return text.Length == 0 ? null : text.Length <= ErrorEvent.MaxBreadcrumbsLength ? text : text[^ErrorEvent.MaxBreadcrumbsLength..];
    }

    private static async Task<IResult> SummaryAsync(int? hours, RequestMetrics metrics, ActiveUsers activeUsers, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var period = TimeSpan.FromHours(Math.Clamp(hours ?? 1, 1, 24));
        var now = time.GetUtcNow();
        var since = now - period;
        var errors = await db.ErrorEvents.Where(e => e.OccurredAtUtc >= since)
            .GroupBy(e => e.Source).Select(g => new { Source = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            version = ApiVersion,
            startedAtUtc = metrics.StartedAtUtc,
            nowUtc = now,
            connections = new { hub = metrics.ActiveHubConnections, media = metrics.ActiveMediaConnections },
            activeUsers = await activeUsers.SummaryAsync(db, cancellationToken),
            errors = new
            {
                app = errors.FirstOrDefault(e => e.Source == ErrorEvent.FromApp)?.Count ?? 0,
                api = errors.FirstOrDefault(e => e.Source == ErrorEvent.FromApi)?.Count ?? 0,
            },
            requests = metrics.Snapshot(period),
        });
    }

    /// <summary>Erros agrupados pelo problema (<see cref="ErrorEvent.Fingerprint"/>), mais frequentes primeiro.</summary>
    private static async Task<IResult> IssuesAsync(int? hours, string? source, AppDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 24 * 30));
        var query = db.ErrorEvents.AsNoTracking().Where(e => e.OccurredAtUtc >= since);
        if (source is ErrorEvent.FromApp or ErrorEvent.FromApi)
        {
            query = query.Where(e => e.Source == source);
        }

        // Agrupa em memória (o SQLite não tem "o último de cada grupo"); a pilha fica de fora.
        var rows = await query.OrderByDescending(e => e.OccurredAtUtc).Take(20_000)
            .Select(e => new { e.Fingerprint, e.Source, e.Level, e.Category, e.Message, e.OccurredAtUtc, e.AppVersion, e.InstallId, e.UserId })
            .ToListAsync(cancellationToken);

        var issues = rows.GroupBy(e => e.Fingerprint).Select(g =>
        {
            var last = g.First();
            return new
            {
                fingerprint = g.Key,
                last.Source,
                last.Level,
                last.Category,
                last.Message,
                count = g.Count(),
                // Quantos computadores (aplicativo) ou usuários (API) foram afetados.
                affected = g.Select(e => e.InstallId?.ToString() ?? e.UserId?.ToString()).Where(x => x is not null).Distinct().Count(),
                versions = g.Select(e => e.AppVersion).Where(v => v is not null).Distinct().OrderDescending().Take(5).ToList(),
                firstSeenUtc = g.Last().OccurredAtUtc,
                lastSeenUtc = last.OccurredAtUtc,
            };
        }).OrderByDescending(i => i.lastSeenUtc).Take(200).ToList();

        return Results.Ok(issues);
    }

    private static async Task<IResult> EventsAsync(string? fingerprint, string? traceId, int? limit, AppDbContext db, CancellationToken cancellationToken)
    {
        var query = db.ErrorEvents.AsNoTracking();
        if (!string.IsNullOrEmpty(fingerprint))
        {
            query = query.Where(e => e.Fingerprint == fingerprint);
        }

        if (!string.IsNullOrEmpty(traceId))
        {
            query = query.Where(e => e.TraceId == traceId);
        }

        var events = await query.OrderByDescending(e => e.OccurredAtUtc).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(cancellationToken);
        return Results.Ok(events);
    }
}
