using System.Net.Http.Json;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ScreenCord.Api.Tests;

/// <summary>Limites do compartilhamento pela API: informados em GET /limits e aplicados no hub.</summary>
public sealed class LimitsTests
{
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

    [Fact]
    public async Task Padrao_540pA30fpsAte1500kbps()
    {
        using var factory = new ApiFactory();

        var limits = await factory.CreateClient().GetFromJsonAsync<ServerLimitsDto>("/limits");

        Assert.Equal(new StreamLimits(540, 30, 1500), limits!.Relay);
        Assert.Equal(StreamLimits.Relay, limits.Relay);
    }

    [Fact]
    public async Task LimitesConfigurados_SaoInformadosEAceitosNoHub()
    {
        using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["RelayLimits:MaxHeight"] = "720",
            ["RelayLimits:MaxFrameRate"] = "60",
            ["RelayLimits:MaxBitrateKbps"] = "3000",
        });
        var (ana, _) = await factory.LoginAsync("token-ana");
        var group = (await (await ana.PostAsJsonAsync("/groups", new GroupCreate("Sala"))).Content.ReadFromJsonAsync<GroupDto>())!;
        await using var hub = await ConnectAsync(factory, ana);
        await hub.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, group.Id);

        Assert.Equal(new StreamLimits(720, 60, 3000), (await ana.GetFromJsonAsync<ServerLimitsDto>("/limits"))!.Relay);
        Assert.NotNull(await hub.InvokeAsync<ScreenShareDto?>(GroupsHubContract.StartScreenShare, new ScreenShareStart(1280, 720, 60, 3000, false)));
        Assert.Null(await hub.InvokeAsync<ScreenShareDto?>(GroupsHubContract.StartScreenShare, new ScreenShareStart(1920, 1080, 30, 3000, false)));
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
}
