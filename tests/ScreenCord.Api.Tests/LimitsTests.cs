using System.Net;
using System.Net.Http.Json;
using ScreenCord.Api.Data;
using ScreenCord.Api.Endpoints;
using ScreenCord.Api.Observability;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Tests;

/// <summary>Limites do compartilhamento pela API: informados em GET /limits, aplicados no hub e ajustáveis pelo painel.</summary>
public sealed class LimitsTests
{
    private const string AdminToken = "token-do-painel";

    private static async Task<HubConnection> ConnectAsync(ApiFactory factory, HttpClient client)
    {
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/groups"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var webSocket = server.CreateWebSocketClient();
                    webSocket.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
                    return await webSocket.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static ApiFactory AdminFactory(Dictionary<string, string?>? settings = null) =>
        new(new Dictionary<string, string?>(settings ?? []) { ["Observability:DashboardToken"] = AdminToken });

    private static HttpClient Admin(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ObservabilityEndpoints.TokenHeader, AdminToken);
        return client;
    }

    /// <summary>Entra numa sala e tenta compartilhar cada combinação; devolve quais o hub aceitou.</summary>
    private static async Task<bool[]> TryShareAsync(ApiFactory factory, params (int Height, int Fps, int Kbps)[] attempts)
    {
        var (ana, _) = await factory.LoginAsync("token-ana");
        var group = (await (await ana.PostAsJsonAsync("/groups", new GroupCreate("Sala"))).Content.ReadFromJsonAsync<GroupDto>())!;
        await using var hub = await ConnectAsync(factory, ana);
        await hub.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, group.Id);

        var accepted = new List<bool>();
        foreach (var (height, fps, kbps) in attempts)
        {
            var start = new ScreenShareStart(height * 16 / 9, height, fps, kbps, false);
            accepted.Add(await hub.InvokeAsync<ScreenShareDto?>(GroupsHubContract.StartScreenShare, start) is not null);
            await hub.InvokeAsync(GroupsHubContract.StopScreenShare);
        }

        return [.. accepted];
    }

    [Fact]
    public async Task Padrao_720pA60fpsOu1080pA30fps_Ate3000kbps()
    {
        using var factory = new ApiFactory();

        var limits = (await factory.CreateClient().GetFromJsonAsync<ServerLimitsDto>("/limits"))!.Relay;

        Assert.Equal(StreamLimits.Relay, limits);
        Assert.Equal([new StreamProfile(720, 60), new StreamProfile(1080, 30)], limits.Profiles);
        Assert.Equal((1080, 60, 3000), (limits.MaxHeight, limits.MaxFrameRate, limits.MaxBitrateKbps));
        Assert.Equal(
            new[] { true, true, true, true, false, false, false },
            await TryShareAsync(factory, (720, 60, 3000), (1080, 30, 3000), (540, 60, 1500), (720, 30, 300), (1080, 60, 3000), (720, 60, 3001), (1440, 30, 1500)));
    }

    [Fact]
    public async Task ConfiguracaoNoFormatoAntigo_UmParSoContinuaValendo()
    {
        using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["RelayLimits:MaxHeight"] = "720",
            ["RelayLimits:MaxFrameRate"] = "60",
            ["RelayLimits:MaxBitrateKbps"] = "3000",
        });
        var (ana, _) = await factory.LoginAsync("token-ana");

        var limits = (await ana.GetFromJsonAsync<ServerLimitsDto>("/limits"))!.Relay;

        Assert.Equal(new StreamLimits(720, 60, 3000), limits);
        Assert.Null(limits.Profiles);
        Assert.Equal(new[] { true, false }, await TryShareAsync(factory, (720, 60, 3000), (1080, 30, 3000)));
    }

    [Fact]
    public async Task ConfiguracaoComCombinacoes_ViraLimite()
    {
        using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["RelayLimits:Profiles:0:Height"] = "540",
            ["RelayLimits:Profiles:0:FrameRate"] = "60",
            ["RelayLimits:Profiles:1:Height"] = "720",
            ["RelayLimits:Profiles:1:FrameRate"] = "30",
            ["RelayLimits:MaxBitrateKbps"] = "2000",
        });

        var limits = (await factory.CreateClient().GetFromJsonAsync<ServerLimitsDto>("/limits"))!.Relay;

        Assert.Equal([new StreamProfile(540, 60), new StreamProfile(720, 30)], limits.Profiles);
        Assert.Equal(2000, limits.MaxBitrateKbps);
        Assert.Equal(new[] { true, true, false }, await TryShareAsync(factory, (540, 60, 2000), (720, 30, 2000), (720, 60, 2000)));
    }

    [Fact]
    public async Task Painel_SemToken_NaoEditaLimites()
    {
        using var factory = AdminFactory();
        var body = new RelayLimitsUpdate([new StreamProfile(1080, 60)], 5000);

        var withoutToken = await factory.CreateClient().PutAsJsonAsync("/admin/api/limits", body);
        var wrongToken = factory.CreateClient();
        wrongToken.DefaultRequestHeaders.Add(ObservabilityEndpoints.TokenHeader, "errado");

        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongToken.DeleteAsync("/admin/api/limits")).StatusCode);
        Assert.Equal(StreamLimits.Relay, (await factory.CreateClient().GetFromJsonAsync<ServerLimitsDto>("/limits"))!.Relay);
    }

    [Fact]
    public async Task Painel_AjustaOsLimites_EValemNaHoraSemDeploy()
    {
        using var factory = AdminFactory();
        var admin = Admin(factory);
        var before = (await admin.GetFromJsonAsync<RelayLimitsView>("/admin/api/limits"))!;
        Assert.False(before.Custom);
        Assert.Equal(StreamLimits.Relay, before.Relay);

        var response = await admin.PutAsJsonAsync("/admin/api/limits", new RelayLimitsUpdate([new StreamProfile(540, 60), new StreamProfile(1080, 60)], 5000));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = (await response.Content.ReadFromJsonAsync<RelayLimitsView>())!;
        Assert.True(after.Custom);
        Assert.NotNull(after.UpdatedAtUtc);
        Assert.Equal(StreamLimits.Relay, after.Defaults);
        Assert.Equal([new StreamProfile(1080, 60)], after.Relay.Profiles); // a combinação menor já é coberta
        Assert.Equal(5000, after.Relay.MaxBitrateKbps);
        Assert.Equal(after.Relay, (await factory.CreateClient().GetFromJsonAsync<ServerLimitsDto>("/limits"))!.Relay);
        Assert.Equal(new[] { true, false }, await TryShareAsync(factory, (1080, 60, 5000), (1080, 60, 5001)));

        var reset = await admin.DeleteAsync("/admin/api/limits");

        Assert.False((await reset.Content.ReadFromJsonAsync<RelayLimitsView>())!.Custom);
        Assert.Equal(StreamLimits.Relay, (await factory.CreateClient().GetFromJsonAsync<ServerLimitsDto>("/limits"))!.Relay);
        Assert.Equal(new[] { false }, await TryShareAsync(factory, (1080, 60, 3000)));
    }

    [Fact]
    public async Task Painel_RecusaLimitesQueOAppNaoSabeFazer()
    {
        using var factory = AdminFactory();
        var admin = Admin(factory);
        var invalid = new RelayLimitsUpdate[]
        {
            new(null, 3000),
            new([], 3000),
            new([new StreamProfile(480, 30)], 3000),
            new([new StreamProfile(720, 45)], 3000),
            new([new StreamProfile(720, 30)], 100),
            new([new StreamProfile(720, 30)], 50_001),
        };

        foreach (var update in invalid)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/api/limits", update)).StatusCode);
        }

        Assert.False((await admin.GetFromJsonAsync<RelayLimitsView>("/admin/api/limits"))!.Custom);
    }

    [Fact]
    public async Task AjusteDoPainel_SobreviveAoReinicio()
    {
        using var factory = AdminFactory();
        await Admin(factory).PutAsJsonAsync("/admin/api/limits", new RelayLimitsUpdate([new StreamProfile(720, 30)], 1500));

        // Uma API que sobe depois lê o mesmo banco e fica com o ajuste, não com os padrões.
        using var scope = factory.Services.CreateScope();
        var restarted = new RelayLimitsStore(factory.Services.GetRequiredService<IOptions<RelayLimitsOptions>>(), TimeProvider.System);
        Assert.Equal(StreamLimits.Relay, restarted.Current);
        restarted.Load(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        Assert.Equal([new StreamProfile(720, 30)], restarted.Current.Profiles);
        Assert.Equal(1500, restarted.Current.MaxBitrateKbps);
        Assert.NotNull(restarted.CustomSince);
    }

    [Fact]
    public void Limites_NoJson_VoltamIguais_EAppAntigoIgnoraAsCombinacoes()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ServerLimitsDto(StreamLimits.Relay), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(StreamLimits.Relay, System.Text.Json.JsonSerializer.Deserialize<ServerLimitsDto>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Relay);
        Assert.Contains("\"maxHeight\":1080", json);
        Assert.Contains("\"maxFrameRate\":60", json);
        Assert.Contains("\"maxBitrateKbps\":3000", json);
    }

    [Theory]
    [InlineData(540, 30, 1500, true)]
    [InlineData(1080, 60, 5000, true)]
    [InlineData(480, 30, 1500, false)]
    [InlineData(1440, 30, 1500, false)]
    [InlineData(540, 45, 1500, false)]
    [InlineData(540, 30, 100, false)]
    public void Limites_ForaDoQueOAppSabeFazer_NaoSaoAceitos(int height, int fps, int kbps, bool sane)
    {
        Assert.Equal(sane, new StreamLimits(height, fps, kbps).IsSane);
    }

    [Fact]
    public void Combinacoes_ForaDoQueOAppSabeFazer_NaoSaoAceitas()
    {
        Assert.True(StreamLimits.Relay.IsSane);
        Assert.False((StreamLimits.Relay with { Profiles = [] }).IsSane);
        Assert.False((StreamLimits.Relay with { Profiles = [new StreamProfile(480, 30)] }).IsSane);
        Assert.False((StreamLimits.Relay with { Profiles = [new StreamProfile(720, 45)] }).IsSane);
    }

    [Theory]
    [InlineData(720, 60, true)]
    [InlineData(1080, 30, true)]
    [InlineData(540, 60, true)] // coberta por 720p a 60 fps
    [InlineData(600, 60, true)] // janela pequena: altura menor que a escolhida
    [InlineData(1080, 60, false)]
    [InlineData(1081, 30, false)]
    [InlineData(720, 45, false)]
    public void Combinacoes_ValemPorCobertura(int height, int fps, bool allowed)
    {
        Assert.Equal(allowed, StreamLimits.Relay.AllowsQuality(height, fps));
    }
}
