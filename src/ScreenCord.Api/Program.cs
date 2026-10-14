using System.Security.Claims;
using System.Threading.RateLimiting;
using ScreenCord.Api;
using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Api.Endpoints;
using ScreenCord.Api.Realtime;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));

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

// Conexões persistentes para a presença nos grupos. O rastreador fica em memória (uma instância da API).
builder.Services.AddSignalR();
builder.Services.AddSingleton<GroupPresence>();
builder.Services.AddSingleton<GroupNotifier>();

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
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Cria o banco (ou as tabelas novas). Para evoluir o esquema em produção, troque por migrações do EF Core.
    DatabaseSchema.Ensure(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

app.MapScreenCordOpenApi();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Infra").WithSummary("Verificação de disponibilidade");
app.MapAuthEndpoints();
app.MapFriendEndpoints();
app.MapGroupEndpoints();
app.MapHub<GroupsHub>(GroupsHubContract.Path).RequireAuthorization();

app.Run();

static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

public partial class Program;
