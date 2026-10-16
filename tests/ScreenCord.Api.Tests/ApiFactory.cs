using System.Net.Http.Headers;
using System.Net.Http.Json;
using ScreenCord.Api.Auth;
using ScreenCord.Api.Billing;
using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ScreenCord.Api.Tests;

/// <summary>Simula o Discord: cada token conhecido corresponde a uma conta.</summary>
public sealed class FakeDiscordVerifier : IDiscordTokenVerifier
{
    public const string UnavailableToken = "discord-fora-do-ar";

    public Dictionary<string, DiscordIdentity> Accounts { get; } = new()
    {
        ["token-ana"] = new("1001", "ana.silva", "Ana", "0", "8342729096ea3675442027381ff50dfe"),
        ["token-bruno"] = new("1002", "bruno", null, "0", null),
        ["token-carla"] = new("1003", "carla_dev", "Carla", "0", null),
    };

    public Task<DiscordIdentity> VerifyAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (accessToken == UnavailableToken)
        {
            throw new DiscordVerificationException(DiscordVerificationFailure.Unavailable);
        }

        return Accounts.TryGetValue(accessToken, out var identity)
            ? Task.FromResult(identity)
            : throw new DiscordVerificationException(DiscordVerificationFailure.InvalidToken);
    }
}

/// <summary>Simula o AbacatePay: cada checkout criado fica registrado com o <c>externalId</c> recebido.</summary>
public sealed class FakeAbacatePay : IAbacatePayClient
{
    public List<(string ProductId, string ExternalId, AbacatePayCheckout Checkout)> Checkouts { get; } = [];

    public bool Unavailable { get; set; }

    public Task<AbacatePayCheckout> CreateCheckoutAsync(string productId, string externalId, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new AbacatePayException("fora do ar");
        }

        var id = $"bill_{Checkouts.Count + 1}";
        var checkout = new AbacatePayCheckout(id, $"https://app.abacatepay.com/pay/{id}");
        lock (Checkouts)
        {
            Checkouts.Add((productId, externalId, checkout));
        }

        return Task.FromResult(checkout);
    }
}

/// <summary>API real em memória, com SQLite em memória, o Discord e o AbacatePay simulados.</summary>
/// <param name="settings">Configurações a mais (ex.: <c>Plans:ProDiscordIds:0</c>), acima das do appsettings.</param>
public sealed class ApiFactory(IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FakeDiscordVerifier Discord { get; } = new();

    public FakeAbacatePay AbacatePay { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");

        // Os clientes dos testes não mandam a versão (contam como 0.1.0); a versão mínima tem testes próprios.
        builder.UseSetting("Clients:MinVersion", "0.1.0");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<IDiscordTokenVerifier>();
            services.AddSingleton<IDiscordTokenVerifier>(Discord);

            services.RemoveAll<IAbacatePayClient>();
            services.AddSingleton<IAbacatePayClient>(AbacatePay);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }

    /// <summary>Faz login com um token simulado e devolve um cliente já autenticado na API.</summary>
    public async Task<(HttpClient Client, UserDto User)> LoginAsync(string discordToken)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/auth/discord", new DiscordLoginRequest(discordToken));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return (client, login.User);
    }
}
