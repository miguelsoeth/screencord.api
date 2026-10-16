using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Channels;
using ScreenCord.Api.Billing;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ScreenCord.Api.Tests;

/// <summary>Planos: limites do gratuito e do Pro, Pro concedido à mão e compra pelo AbacatePay.</summary>
public sealed class PlanTests : IAsyncLifetime
{
    private const string WebhookSecret = "segredo-do-webhook";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly Dictionary<string, string?> Billing = new()
    {
        ["AbacatePay:ApiKey"] = "abc_dev_chave",
        ["AbacatePay:WebhookSecret"] = WebhookSecret,
        ["AbacatePay:DevMode"] = "true",
        ["AbacatePay:Offers:0:Id"] = "pro-30",
        ["AbacatePay:Offers:0:Name"] = "Pro por 30 dias",
        ["AbacatePay:Offers:0:Days"] = "30",
        ["AbacatePay:Offers:0:PriceCents"] = "1490",
        ["AbacatePay:Offers:0:ProductId"] = "prod_30",
        // O webhook não manda a versão do app: não pode cair na versão mínima.
        ["Clients:MinVersion"] = "0.5.0",
    };

    private readonly List<HubConnection> _connections = [];
    private ApiFactory _factory = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        _factory?.Dispose();
    }

    private ApiFactory Start(Dictionary<string, string?>? settings = null)
    {
        _factory = new ApiFactory(settings);
        return _factory;
    }

    /// <summary>Login com o cabeçalho de versão (a versão mínima pode estar acima de 0.1.0 nestes testes).</summary>
    /// <param name="ip">IP de origem (com <c>Proxy:TrustForwardedHeaders</c>), para fugir do limite de logins por IP.</param>
    private async Task<(HttpClient Client, UserDto User)> LoginAsync(string token, string? ip = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientVersionContract.Header, "0.0.0-dev");
        if (ip is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        }

        var login = (await (await client.PostAsJsonAsync("/auth/discord", new DiscordLoginRequest(token))).Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
        return (client, login.User);
    }

    private static async Task<PlanDto> PlanAsync(HttpClient client) => (await client.GetFromJsonAsync<PlanDto>("/me/plan"))!;

    private static async Task MakeFriendsAsync(HttpClient a, UserDto aUser, HttpClient b, UserDto bUser)
    {
        (await a.PostAsJsonAsync("/friends/requests", new FriendRequestCreate(bUser.Id))).EnsureSuccessStatusCode();
        (await b.PostAsync($"/friends/requests/{aUser.Id}/accept", null)).EnsureSuccessStatusCode();
    }

    private static async Task<GroupDto> CreateGroupAsync(HttpClient client, string name = "Sala") =>
        (await (await client.PostAsJsonAsync("/groups", new GroupCreate(name))).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<GroupDto>())!;

    private async Task<HubConnection> ConnectHubAsync(HttpClient client)
    {
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/groups"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Headers[ClientVersionContract.Header] = "0.0.0-dev";
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var webSocket = server.CreateWebSocketClient();
                    webSocket.ConfigureRequest = request =>
                    {
                        request.Headers.Authorization = "Bearer " + token;
                        request.Headers[ClientVersionContract.Header] = "0.0.0-dev";
                    };
                    return await webSocket.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    private static string CompletedEvent(string checkoutId, string externalId, bool devMode = true, string name = "checkout.completed") =>
        $$"""
        {
          "id": "log_{{Guid.NewGuid():N}}",
          "event": "{{name}}",
          "apiVersion": 2,
          "devMode": {{(devMode ? "true" : "false")}},
          "data": {
            "checkout": { "id": "{{checkoutId}}", "externalId": "{{externalId}}", "amount": 1490, "paidAmount": 1490, "status": "PAID", "items": [{ "id": "prod_30", "quantity": 1 }] },
            "customer": null,
            "payerInformation": { "method": "PIX" }
          }
        }
        """;

    /// <summary>Envia o webhook como o AbacatePay: secret na URL e corpo assinado, sem cabeçalho de versão.</summary>
    private Task<HttpResponseMessage> SendWebhookAsync(string body, string secret = WebhookSecret, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/billing/abacatepay/webhook?webhookSecret={Uri.EscapeDataString(secret)}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Signature", signature ?? AbacatePayWebhook.Sign(AbacatePayOptions.DefaultSignatureKey, Encoding.UTF8.GetBytes(body)));
        return _factory.CreateClient().SendAsync(request);
    }

    private async Task<(string CheckoutId, string ExternalId)> CheckoutAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/billing/checkout", new ProCheckoutCreate("pro-30"));
        response.EnsureSuccessStatusCode();
        var dto = (await response.Content.ReadFromJsonAsync<ProCheckoutDto>())!;
        var created = _factory.AbacatePay.Checkouts.Single(c => c.Checkout.Url == dto.Url);
        Assert.Equal("prod_30", created.ProductId);
        return (created.Checkout.Id, created.ExternalId);
    }

    [Fact]
    public async Task Plano_SemCompra_EhGratuitoESemOfertas()
    {
        Start();
        var (ana, _) = await LoginAsync("token-ana");

        var plan = await PlanAsync(ana);

        Assert.Equal(PlanTier.Free, plan.Tier);
        Assert.Equal(PlanLimits.Free, plan.Limits);
        Assert.Null(plan.ProUntilUtc);
        Assert.Empty(plan.Offers);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ana.PostAsJsonAsync("/billing/checkout", new ProCheckoutCreate("pro-30"))).StatusCode);
    }

    [Fact]
    public async Task ProConcedidoAMao_TemOsLimitesDoPro()
    {
        Start(new() { ["Plans:ProDiscordIds:0"] = "1001" });
        var (ana, _) = await LoginAsync("token-ana");
        var (bruno, _) = await LoginAsync("token-bruno");

        Assert.Equal(PlanTier.Pro, (await PlanAsync(ana)).Tier);
        Assert.Equal(PlanLimits.Pro, (await PlanAsync(ana)).Limits);
        Assert.Equal(PlanTier.Free, (await PlanAsync(bruno)).Tier);
    }

    [Fact]
    public async Task Pro_CriaMaisGruposQueOGratuito()
    {
        Start(new() { ["Plans:ProDiscordIds:0"] = "1001" });
        var (ana, _) = await LoginAsync("token-ana");

        for (var i = 0; i <= PlanLimits.Free.MaxCreatedGroups; i++)
        {
            await CreateGroupAsync(ana, $"Grupo {i}");
        }
    }

    [Fact]
    public async Task IntegrantesAlemDoGratuito_SoComOProDeQuemCriou()
    {
        Start(new() { ["Plans:ProDiscordIds:0"] = "1001", ["Proxy:TrustForwardedHeaders"] = "true" });
        var (ana, anaUser) = await LoginAsync("token-ana");
        var (bruno, brunoUser) = await LoginAsync("token-bruno");
        var proGroup = await CreateGroupAsync(ana, "Da Ana");
        var freeGroup = await CreateGroupAsync(bruno, "Do Bruno");
        await MakeFriendsAsync(ana, anaUser, bruno, brunoUser);
        (await bruno.PostAsJsonAsync($"/groups/{freeGroup.Id}/members", new GroupMemberCreate(anaUser.Id))).EnsureSuccessStatusCode();

        // Amigos da Ana que entram nos dois grupos (adicionados por ela, que é integrante de ambos).
        for (var i = 0; i < PlanLimits.Free.MaxMembers; i++)
        {
            _factory.Discord.Accounts[$"token-u{i}"] = new($"2{i:000}", $"usuario{i}", null, "0", null);
            var (friend, friendUser) = await LoginAsync($"token-u{i}", $"10.0.0.{i + 1}");
            await MakeFriendsAsync(ana, anaUser, friend, friendUser);
            (await ana.PostAsJsonAsync($"/groups/{proGroup.Id}/members", new GroupMemberCreate(friendUser.Id))).EnsureSuccessStatusCode();

            var toFree = await ana.PostAsJsonAsync($"/groups/{freeGroup.Id}/members", new GroupMemberCreate(friendUser.Id));
            // O grupo do Bruno já tem Bruno e Ana: lota com 8 amigos.
            Assert.Equal(i < PlanLimits.Free.MaxMembers - 2 ? HttpStatusCode.OK : HttpStatusCode.Conflict, toFree.StatusCode);
        }

        var details = (await ana.GetFromJsonAsync<GroupDetailsDto>($"/groups/{proGroup.Id}"))!;
        Assert.Equal(PlanLimits.Free.MaxMembers + 1, details.Members.Count);
    }

    [Theory]
    [InlineData(1920, 1080, 60, 5000, true)]
    [InlineData(1920, 1080, 30, 5000, true)]
    [InlineData(1280, 720, 60, 4000, true)]
    [InlineData(1920, 1080, 60, 5500, false)]
    [InlineData(2560, 1440, 30, 5000, false)]
    public async Task Compartilhar_ComPro_AceitaAsQualidadesDoPro(int width, int height, int fps, int kbps, bool accepted)
    {
        Start(new() { ["Plans:ProDiscordIds:0"] = "1001" });
        var (ana, _) = await LoginAsync("token-ana");
        var group = await CreateGroupAsync(ana);
        var hub = await ConnectHubAsync(ana);
        await hub.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, group.Id);

        var share = await hub.InvokeAsync<ScreenShareDto?>(GroupsHubContract.StartScreenShare, new ScreenShareStart(width, height, fps, kbps, false));

        Assert.Equal(accepted, share is not null);
    }

    [Fact]
    public async Task Compartilhar_SemPro_RecusaAsQualidadesDoPro()
    {
        Start();
        var (ana, _) = await LoginAsync("token-ana");
        var group = await CreateGroupAsync(ana);
        var hub = await ConnectHubAsync(ana);
        await hub.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, group.Id);

        Assert.Null(await hub.InvokeAsync<ScreenShareDto?>(GroupsHubContract.StartScreenShare, new ScreenShareStart(1920, 1080, 30, 3000, false)));
        Assert.Null(await hub.InvokeAsync<ScreenShareDto?>(GroupsHubContract.StartScreenShare, new ScreenShareStart(1280, 720, 30, 5000, false)));
    }

    [Fact]
    public async Task Compra_PagamentoConfirmado_AtivaOProEAvisaPeloHub()
    {
        Start(Billing);
        var (ana, _) = await LoginAsync("token-ana");
        var plans = Channel.CreateUnbounded<PlanDto>();
        var hub = await ConnectHubAsync(ana);
        hub.On<PlanDto>(GroupsHubContract.PlanChanged, p => plans.Writer.TryWrite(p));

        var offer = Assert.Single((await PlanAsync(ana)).Offers);
        Assert.Equal(new ProOfferDto("pro-30", "Pro por 30 dias", 30, 1490), offer);

        var (checkoutId, externalId) = await CheckoutAsync(ana);
        Assert.Equal(PlanTier.Free, (await PlanAsync(ana)).Tier);

        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(CompletedEvent(checkoutId, externalId))).StatusCode);

        using var timeout = new CancellationTokenSource(Timeout);
        var changed = await plans.Reader.ReadAsync(timeout.Token);
        Assert.Equal(PlanTier.Pro, changed.Tier);
        var plan = await PlanAsync(ana);
        Assert.Equal(PlanTier.Pro, plan.Tier);
        Assert.InRange(plan.ProUntilUtc!.Value, DateTimeOffset.UtcNow.AddDays(29.9), DateTimeOffset.UtcNow.AddDays(30.1));
    }

    [Fact]
    public async Task Compra_WebhookRepetido_NaoSomaDuasVezes_ESegundaCompraEmenda()
    {
        Start(Billing);
        var (ana, _) = await LoginAsync("token-ana");

        var (first, firstExternal) = await CheckoutAsync(ana);
        var body = CompletedEvent(first, firstExternal);
        await SendWebhookAsync(body);
        await SendWebhookAsync(body);
        var afterFirst = (await PlanAsync(ana)).ProUntilUtc!.Value;
        Assert.InRange(afterFirst, DateTimeOffset.UtcNow.AddDays(29.9), DateTimeOffset.UtcNow.AddDays(30.1));

        var (second, secondExternal) = await CheckoutAsync(ana);
        await SendWebhookAsync(CompletedEvent(second, secondExternal));
        Assert.Equal(afterFirst.AddDays(30), (await PlanAsync(ana)).ProUntilUtc!.Value, TimeSpan.FromSeconds(5));

        // Estorno da segunda compra tira os 30 dias dela.
        await SendWebhookAsync(CompletedEvent(second, secondExternal, name: "checkout.refunded"));
        Assert.Equal(afterFirst, (await PlanAsync(ana)).ProUntilUtc!.Value, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Estorno_DaUnicaCompra_VoltaAoGratuito()
    {
        Start(Billing);
        var (ana, _) = await LoginAsync("token-ana");
        var (checkoutId, externalId) = await CheckoutAsync(ana);
        await SendWebhookAsync(CompletedEvent(checkoutId, externalId));

        await SendWebhookAsync(CompletedEvent(checkoutId, externalId, name: "checkout.disputed"));

        Assert.Equal(PlanTier.Free, (await PlanAsync(ana)).Tier);
    }

    [Fact]
    public async Task Webhook_SecretOuAssinaturaErrados_EhRecusadoENaoAtiva()
    {
        Start(Billing);
        var (ana, _) = await LoginAsync("token-ana");
        var (checkoutId, externalId) = await CheckoutAsync(ana);
        var body = CompletedEvent(checkoutId, externalId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWebhookAsync(body, secret: "outro")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWebhookAsync(body, signature: AbacatePayWebhook.Sign("outra-chave", Encoding.UTF8.GetBytes(body)))).StatusCode);
        Assert.Equal(PlanTier.Free, (await PlanAsync(ana)).Tier);
    }

    [Fact]
    public async Task Webhook_DeOutroAmbienteOuDeCheckoutDesconhecido_EhIgnorado()
    {
        Start(Billing);
        var (ana, _) = await LoginAsync("token-ana");
        var (checkoutId, externalId) = await CheckoutAsync(ana);

        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(CompletedEvent(checkoutId, externalId, devMode: false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(CompletedEvent("bill_desconhecido", externalId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(CompletedEvent(checkoutId, Guid.NewGuid().ToString()))).StatusCode);

        Assert.Equal(PlanTier.Free, (await PlanAsync(ana)).Tier);
    }

    [Fact]
    public async Task Checkout_OfertaInexistente_Retorna400_EAbacatePayForaDoAr_Retorna503()
    {
        Start(Billing);
        var (ana, _) = await LoginAsync("token-ana");

        Assert.Equal(HttpStatusCode.BadRequest, (await ana.PostAsJsonAsync("/billing/checkout", new ProCheckoutCreate("pro-999"))).StatusCode);

        _factory.AbacatePay.Unavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ana.PostAsJsonAsync("/billing/checkout", new ProCheckoutCreate("pro-30"))).StatusCode);
    }

    [Fact]
    public void Limites_DoGratuito_SaoOsQueAsVersoesAntigasConhecem()
    {
        Assert.Equal(GroupsHubContract.MaxMembers, PlanLimits.Free.MaxMembers);
        Assert.Equal(GroupsHubContract.MaxCreatedGroups, PlanLimits.Free.MaxCreatedGroups);
        Assert.Equal(ScreenShareLimits.MaxBitrateKbps, PlanLimits.Free.MaxBitrateKbps);
        Assert.Equal(2 * PlanLimits.Free.MaxMembers, PlanLimits.Pro.MaxMembers);
        Assert.Equal(5000, PlanLimits.Pro.MaxBitrateKbps);
        Assert.True(PlanLimits.Pro.AllowsQuality(720, 60));
        Assert.True(PlanLimits.Pro.AllowsQuality(1080, 60));
        Assert.False(PlanLimits.Free.AllowsQuality(720, 60));
        Assert.False(PlanLimits.Free.AllowsQuality(1080, 30));
    }
}
