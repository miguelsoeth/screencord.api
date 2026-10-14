using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Tests;

public sealed class AuthTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<T> QueryDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task PrimeiroLogin_CriaOUsuarioComOsDadosDoDiscord()
    {
        var (_, user) = await _factory.LoginAsync("token-ana");

        Assert.Equal("1001", user.DiscordId);
        Assert.Equal("ana.silva", user.Username);
        Assert.Equal("Ana", user.GlobalName);
        Assert.Equal("8342729096ea3675442027381ff50dfe", user.AvatarHash);
        Assert.Equal(1, await QueryDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task LoginRepetido_NaoDuplicaEAtualizaOsDados()
    {
        var (_, first) = await _factory.LoginAsync("token-ana");
        _factory.Discord.Accounts["token-ana"] = new DiscordIdentity("1001", "ana.nova", "Ana Nova", "0", null);

        var (_, second) = await _factory.LoginAsync("token-ana");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("ana.nova", second.Username);
        Assert.Null(second.AvatarHash);
        Assert.Equal(1, await QueryDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task TokenRecusadoPeloDiscord_Retorna401ENaoCriaUsuario()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/discord", new DiscordLoginRequest("token-falso"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_discord_token", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code);
        Assert.Equal(0, await QueryDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task DiscordIndisponivel_Retorna503()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/discord", new DiscordLoginRequest(FakeDiscordVerifier.UnavailableToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task TokenVazio_Retorna400()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/discord", new DiscordLoginRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SessaoEhGuardadaSomenteComoHash()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/discord", new DiscordLoginRequest("token-ana"));
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;

        var stored = await QueryDbAsync(db => db.Sessions.SingleAsync());

        Assert.NotEqual(login.Token, stored.TokenHash);
        Assert.Equal(SessionTokens.Hash(login.Token), stored.TokenHash);
    }

    [Theory]
    [InlineData("/me")]
    [InlineData("/friends")]
    [InlineData("/friends/requests")]
    [InlineData("/users/search?q=ana")]
    public async Task RotasProtegidas_SemToken_Retornam401(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenDeSessaoInventado_Retorna401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('A', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task TokenDoDiscordNaoServeComoSessaoDaApi()
    {
        await _factory.LoginAsync("token-ana");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "token-ana");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Me_RetornaOUsuarioDaSessao()
    {
        var (client, user) = await _factory.LoginAsync("token-bruno");

        var me = await client.GetFromJsonAsync<UserDto>("/me");

        Assert.Equal(user.Id, me!.Id);
        Assert.Equal("bruno", me.Username);
    }

    [Fact]
    public async Task Logout_InvalidaASessao()
    {
        var (client, _) = await _factory.LoginAsync("token-ana");

        var logout = await client.PostAsync("/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task SessaoExpirada_Retorna401()
    {
        var (client, _) = await _factory.LoginAsync("token-ana");
        await QueryDbAsync(async db =>
        {
            var session = await db.Sessions.SingleAsync();
            session.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            return await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }
}

public class DiscordTokenVerifierTests
{
    private const string ClientId = "1559739734652223518";

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Body(string applicationId = ClientId, string scopes = "\"identify\"", string user = """{"id":"268473310986240001","username":"discord","avatar":"f749bb0cbeeb26ef21eca719337d20f1","discriminator":"0","global_name":"Discord"}""") =>
        $$"""{"application":{"id":"{{applicationId}}","name":"x"},"scopes":[{{scopes}}],"expires":"2027-01-23T02:33:17+00:00","user":{{user}}}""";

    private static (DiscordTokenVerifier Verifier, StubHandler Handler) Create(HttpStatusCode status, string body, string clientId = ClientId)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://discord.com/api/v10/") };
        return (new DiscordTokenVerifier(http, Options.Create(new DiscordOptions { ClientId = clientId })), handler);
    }

    [Fact]
    public async Task TokenDoAplicativoCerto_RetornaAConta()
    {
        var (verifier, handler) = Create(HttpStatusCode.OK, Body());

        var identity = await verifier.VerifyAsync("abc", CancellationToken.None);

        Assert.Equal("https://discord.com/api/v10/oauth2/@me", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer abc", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("268473310986240001", identity.Id);
        Assert.Equal("discord", identity.Username);
        Assert.Equal("Discord", identity.GlobalName);
    }

    [Fact]
    public async Task TokenEmitidoParaOutroAplicativo_EhRecusado()
    {
        var (verifier, _) = Create(HttpStatusCode.OK, Body(applicationId: "999999999999999999"));

        var ex = await Assert.ThrowsAsync<DiscordVerificationException>(() => verifier.VerifyAsync("abc", CancellationToken.None));

        Assert.Equal(DiscordVerificationFailure.InvalidToken, ex.Failure);
    }

    [Theory]
    [InlineData("\"guilds\"", """{"id":"1","username":"x"}""")]
    [InlineData("\"identify\"", "null")]
    [InlineData("\"identify\"", """{"id":"nao-numerico","username":"x"}""")]
    [InlineData("\"identify\"", """{"id":"1","username":""}""")]
    [InlineData("\"identify\"", """{"id":"1","username":"x","avatar":"../../x"}""")]
    public async Task RespostaSemContaValida_EhRecusada(string scopes, string user)
    {
        var (verifier, _) = Create(HttpStatusCode.OK, Body(scopes: scopes, user: user));

        var ex = await Assert.ThrowsAsync<DiscordVerificationException>(() => verifier.VerifyAsync("abc", CancellationToken.None));

        Assert.Equal(DiscordVerificationFailure.InvalidToken, ex.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DiscordVerificationFailure.InvalidToken)]
    [InlineData(HttpStatusCode.TooManyRequests, DiscordVerificationFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, DiscordVerificationFailure.Unavailable)]
    public async Task ErrosHttp_SaoClassificados(HttpStatusCode status, DiscordVerificationFailure expected)
    {
        var (verifier, _) = Create(status, "{}");

        var ex = await Assert.ThrowsAsync<DiscordVerificationException>(() => verifier.VerifyAsync("abc", CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task SemClientIdNoServidor_NaoConsultaODiscord()
    {
        var (verifier, handler) = Create(HttpStatusCode.OK, Body(), clientId: "");

        var ex = await Assert.ThrowsAsync<DiscordVerificationException>(() => verifier.VerifyAsync("abc", CancellationToken.None));

        Assert.Equal(DiscordVerificationFailure.NotConfigured, ex.Failure);
        Assert.Null(handler.Request);
    }
}
