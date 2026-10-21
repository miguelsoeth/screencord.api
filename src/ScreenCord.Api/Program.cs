using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ScreenCord.Api;
using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Api.Endpoints;
using ScreenCord.Api.Observability;
using ScreenCord.Api.Realtime;
using ScreenCord.Api.Versioning;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));
builder.Services.AddOptions<RelayLimitsOptions>()
    .Bind(builder.Configuration.GetSection(RelayLimitsOptions.SectionName))
    .Validate(o => o.ToLimits().IsSane, "RelayLimits fora do que o aplicativo sabe fazer.")
    .ValidateOnStart();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=screencord.db"));

builder.Services.AddHttpClient<IDiscordTokenVerifier, DiscordTokenVerifier>(client =>
{
    client.BaseAddress = new Uri("https://discord.com/api/v10/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://localhost/screencord, 1.0.0)");
});

builder.Services
    .AddAuthentication(SessionTokens.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionTokens.Scheme, null);
builder.Services.AddAuthorization();
builder.Services.AddScreenCordOpenApi();

// Métricas das requisições, erros da API e do aplicativo e o painel em /admin.
builder.Services.AddScreenCordObservability(builder.Configuration);

// Conexões persistentes para a presença nos grupos. O rastreador fica em memória (uma instância da API).
builder.Services.AddSignalR();
builder.Services.AddSingleton<GroupPresence>();
builder.Services.AddSingleton<GroupNotifier>();
builder.Services.AddSingleton<FriendNotifier>();
builder.Services.AddSingleton<MediaRelay>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Limite geral por usuário autenticado (ou por IP, antes do login).
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ClientIp(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));

    // O login consulta o Discord a cada chamada; fica com um limite mais apertado por IP.
    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientIp(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

    options.AddTelemetryRateLimit(ClientIp);
});

// Em produção a API fica atrás do Caddy, que é o único a falar com ela (a porta não é publicada).
// Sem isto, todas as requisições viriam do IP do proxy e o rate limit por IP valeria para todos juntos.
var behindProxy = builder.Configuration.GetValue<bool>("Proxy:TrustForwardedHeaders");
if (behindProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    .Split('+')[0] ?? "desconhecida";

// Versões antigas do app são atendidas até a mínima; abaixo dela recebem 426 e se atualizam.
var minClientVersion = ClientVersionGate.ParseMinVersion(
    builder.Configuration.GetSection(ClientVersionOptions.SectionName).Get<ClientVersionOptions>()?.MinVersion
    ?? new ClientVersionOptions().MinVersion);

var app = builder.Build();

if (behindProxy)
{
    app.UseForwardedHeaders();
}

using (var scope = app.Services.CreateScope())
{
    // Cria o banco (ou as tabelas novas). Para evoluir o esquema em produção, troque por migrações do EF Core.
    DatabaseSchema.Ensure(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

app.MapScreenCordOpenApi();
// Antes da versão mínima, para medir também as requisições recusadas por ela.
app.UseRequestMetrics();
app.UseClientVersionGate(minClientVersion);

// Canal de mídia do compartilhamento de tela. O ping mantém a conexão viva atrás de proxies.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

app.UseAuthentication();
app.UseActiveUsers();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version, minClientVersion = minClientVersion.ToString(3) })).WithTags("Infra").WithSummary("Verificação de disponibilidade");
app.MapAuthEndpoints();
app.MapFriendEndpoints();
app.MapGroupEndpoints();
app.MapLimitsEndpoints();
app.MapHub<GroupsHub>(GroupsHubContract.Path).RequireAuthorization();
app.MapMediaEndpoints();
app.MapObservabilityEndpoints();

app.Run();

static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

public partial class Program;
