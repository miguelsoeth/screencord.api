using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Threading.Channels;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ScreenCord.Api.Tests;

/// <summary>Compartilhamento de tela: anúncio pelo hub e repasse da mídia pelo WebSocket de mídia.</summary>
public sealed class ScreenShareTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly ScreenShareStart Hd = new(1280, 720, 30, 3000, true);

    private readonly ApiFactory _factory = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private HttpClient _ana = null!;
    private HttpClient _bruno = null!;
    private UserDto _anaUser = null!;
    private UserDto _brunoUser = null!;
    private GroupDto _group = null!;

    public async Task InitializeAsync()
    {
        (_ana, _anaUser) = await _factory.LoginAsync("token-ana");
        (_bruno, _brunoUser) = await _factory.LoginAsync("token-bruno");

        (await _ana.PostAsJsonAsync("/friends/requests", new FriendRequestCreate(_brunoUser.Id))).EnsureSuccessStatusCode();
        (await _bruno.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null)).EnsureSuccessStatusCode();
        _group = (await (await _ana.PostAsJsonAsync("/groups", new GroupCreate("Sala"))).Content.ReadFromJsonAsync<GroupDto>())!;
        (await _ana.PostAsJsonAsync($"/groups/{_group.Id}/members", new GroupMemberCreate(_brunoUser.Id))).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        _factory.Dispose();
    }

    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    private async Task<Hub> ConnectHubAsync(HttpClient client)
    {
        var token = Token(client);
        var server = _factory.Server;
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
        _disposables.Add(connection);

        var hub = new Hub(connection);
        await connection.StartAsync();
        return hub;
    }

    private async Task<WebSocket> ConnectMediaAsync(HttpClient client, Guid sharerId)
    {
        var token = Token(client);
        var webSocket = _factory.Server.CreateWebSocketClient();
        webSocket.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        var socket = await webSocket.ConnectAsync(MediaContract.BuildUri(_factory.Server.BaseAddress, _group.Id, sharerId), CancellationToken.None);
        _disposables.Add(new SocketDisposer(socket));
        return socket;
    }

    private static byte[] Packet(byte type, bool keyframe, params byte[] payload)
    {
        var packet = new byte[MediaContract.HeaderSize + payload.Length];
        MediaContract.WriteHeader(packet, type, keyframe ? MediaContract.KeyframeFlag : (byte)0, 1234);
        payload.CopyTo(packet, MediaContract.HeaderSize);
        return packet;
    }

    private static Task SendAsync(WebSocket socket, byte[] packet) =>
        socket.SendAsync(packet, WebSocketMessageType.Binary, true, CancellationToken.None);

    /// <summary>Lê a próxima mensagem; null se a conexão foi fechada.</summary>
    private static async Task<byte[]?> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var buffer = new byte[64 * 1024];
        var length = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
            length += result.Count;
        }
        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

        return result.MessageType == WebSocketMessageType.Close ? null : buffer[..length];
    }

    [Fact]
    public async Task Compartilhar_SemEstarNaSala_RetornaNull()
    {
        var hub = await ConnectHubAsync(_ana);

        Assert.Null(await hub.StartAsync(Hd));
    }

    [Theory]
    [InlineData(1280, 720, 45, 3000)]
    [InlineData(1280, 720, 30, 3500)]
    [InlineData(1280, 720, 30, 200)]
    [InlineData(1280, 720, 60, 3000)]   // 60 fps só até 540p
    [InlineData(1920, 1080, 30, 3000)]  // 1080p fica para o plano pago
    [InlineData(1280, 1440, 30, 3000)]
    [InlineData(1281, 720, 30, 3000)]
    public async Task Compartilhar_ConfiguracaoForaDasOpcoes_RetornaNull(int width, int height, int fps, int kbps)
    {
        var hub = await ConnectHubAsync(_ana);
        await hub.JoinAsync(_group.Id);

        Assert.Null(await hub.StartAsync(new ScreenShareStart(width, height, fps, kbps, false)));
    }

    [Fact]
    public async Task Compartilhar_AnunciaATodosEParar_Remove()
    {
        var ana = await ConnectHubAsync(_ana);
        var bruno = await ConnectHubAsync(_bruno);
        await ana.JoinAsync(_group.Id);
        await bruno.JoinAsync(_group.Id);

        var share = await ana.StartAsync(Hd);

        Assert.Equal(new ScreenShareDto(_anaUser.Id, 1280, 720, 30, 3000, true), share);
        var announced = await bruno.NextPresenceAsync(p => p.ScreenShares?.Count == 1);
        Assert.Equal(share, announced.ScreenShares![0]);
        var listed = Assert.Single((await _bruno.GetFromJsonAsync<List<GroupDto>>("/groups"))!);
        Assert.Equal(share, Assert.Single(listed.ScreenShares!));

        await ana.Connection.InvokeAsync(GroupsHubContract.StopScreenShare);

        await bruno.NextPresenceAsync(p => p.ScreenShares is { Count: 0 });
    }

    [Fact]
    public async Task Midia_ChegaAQuemAssiste_ComecandoPorUmQuadroChave()
    {
        var ana = await ConnectHubAsync(_ana);
        var bruno = await ConnectHubAsync(_bruno);
        await ana.JoinAsync(_group.Id);
        await bruno.JoinAsync(_group.Id);
        await ana.StartAsync(Hd);
        var publisher = await ConnectMediaAsync(_ana, _anaUser.Id);

        var viewer = await ConnectMediaAsync(_bruno, _anaUser.Id);

        // Quem compartilha recebe o pedido de quadro-chave para o novo espectador.
        var request = await ReceiveAsync(publisher);
        Assert.Equal(MediaContract.KeyframeRequest, request![0]);

        // Quadros que dependem de anteriores não servem para quem acabou de chegar.
        await SendAsync(publisher, Packet(MediaContract.Video, keyframe: false, 9, 9));
        var keyframe = Packet(MediaContract.Video, keyframe: true, 1, 2, 3);
        var audio = Packet(MediaContract.Audio, keyframe: false, 7);
        var delta = Packet(MediaContract.Video, keyframe: false, 4, 5);
        await SendAsync(publisher, keyframe);
        await SendAsync(publisher, audio);
        await SendAsync(publisher, delta);

        Assert.Equal(keyframe, await ReceiveAsync(viewer));
        Assert.Equal(audio, await ReceiveAsync(viewer));
        Assert.Equal(delta, await ReceiveAsync(viewer));
    }

    [Fact]
    public async Task VideoPausado_SoOAudioChega_EAoVoltarRecomecaPorUmQuadroChave()
    {
        var ana = await ConnectHubAsync(_ana);
        var bruno = await ConnectHubAsync(_bruno);
        await ana.JoinAsync(_group.Id);
        await bruno.JoinAsync(_group.Id);
        await ana.StartAsync(Hd);
        var publisher = await ConnectMediaAsync(_ana, _anaUser.Id);
        var viewer = await ConnectMediaAsync(_bruno, _anaUser.Id);
        Assert.Equal(MediaContract.KeyframeRequest, (await ReceiveAsync(publisher))![0]);
        await SendAsync(publisher, Packet(MediaContract.Video, keyframe: true, 1));
        Assert.Equal(MediaContract.Video, (await ReceiveAsync(viewer))![0]);

        await SendAsync(viewer, Packet(MediaContract.PauseVideo, keyframe: false));

        // A pausa é processada em paralelo: espera até um áudio chegar sem o vídeo enviado antes dele.
        for (byte marker = 0; ; marker++)
        {
            await SendAsync(publisher, Packet(MediaContract.Video, keyframe: false, 9));
            await SendAsync(publisher, Packet(MediaContract.Audio, keyframe: false, marker));
            var first = await ReceiveAsync(viewer);
            if (first![0] == MediaContract.Audio)
            {
                break;
            }

            Assert.Equal(MediaContract.Audio, (await ReceiveAsync(viewer))![0]);
        }

        var keyframe = Packet(MediaContract.Video, keyframe: true, 1, 2, 3);
        var audio = Packet(MediaContract.Audio, keyframe: false, 7);
        await SendAsync(publisher, keyframe);
        await SendAsync(publisher, audio);
        Assert.Equal(audio, await ReceiveAsync(viewer));

        // Pedidos de quadro-chave chegam a quem compartilha no máximo a cada meio segundo.
        await Task.Delay(600);
        await SendAsync(viewer, Packet(MediaContract.ResumeVideo, keyframe: false));
        Assert.Equal(MediaContract.KeyframeRequest, (await ReceiveAsync(publisher))![0]);

        var delta = Packet(MediaContract.Video, keyframe: false, 4, 5);
        await SendAsync(publisher, delta);
        await SendAsync(publisher, keyframe);
        Assert.Equal(keyframe, await ReceiveAsync(viewer));
    }

    [Fact]
    public async Task Assistir_SemEstarNaSala_EhRecusado()
    {
        var ana = await ConnectHubAsync(_ana);
        await ana.JoinAsync(_group.Id);
        await ana.StartAsync(Hd);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => ConnectMediaAsync(_bruno, _anaUser.Id));
        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task EnviarMidia_SemAnunciarOCompartilhamento_EhRecusado()
    {
        var ana = await ConnectHubAsync(_ana);
        await ana.JoinAsync(_group.Id);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => ConnectMediaAsync(_ana, _anaUser.Id));
        Assert.Contains("409", error.Message);
    }

    [Fact]
    public async Task QuemCompartilhaSaiDaSala_EncerraParaQuemAssiste()
    {
        var ana = await ConnectHubAsync(_ana);
        var bruno = await ConnectHubAsync(_bruno);
        await ana.JoinAsync(_group.Id);
        await bruno.JoinAsync(_group.Id);
        await ana.StartAsync(Hd);
        var publisher = await ConnectMediaAsync(_ana, _anaUser.Id);
        var viewer = await ConnectMediaAsync(_bruno, _anaUser.Id);
        Assert.Equal(MediaContract.KeyframeRequest, (await ReceiveAsync(publisher))![0]);

        await ana.Connection.InvokeAsync(GroupsHubContract.LeaveGroup);

        Assert.Null(await ReceiveAsync(viewer));
        await bruno.NextPresenceAsync(p => p.ScreenShares is { Count: 0 } && p.ConnectedUserIds.Count == 1);
    }

    [Fact]
    public async Task CanalDeMidiaCai_EncerraOCompartilhamento()
    {
        var ana = await ConnectHubAsync(_ana);
        var bruno = await ConnectHubAsync(_bruno);
        await ana.JoinAsync(_group.Id);
        await bruno.JoinAsync(_group.Id);
        await ana.StartAsync(Hd);
        var publisher = await ConnectMediaAsync(_ana, _anaUser.Id);
        await bruno.NextPresenceAsync(p => p.ScreenShares?.Count == 1);

        await publisher.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        await bruno.NextPresenceAsync(p => p.ScreenShares is { Count: 0 });
    }

    [Fact]
    public async Task NovasConfiguracoes_SubstituemOCompartilhamentoSemEncerrarParaQuemAssiste()
    {
        var ana = await ConnectHubAsync(_ana);
        var bruno = await ConnectHubAsync(_bruno);
        await ana.JoinAsync(_group.Id);
        await bruno.JoinAsync(_group.Id);
        await ana.StartAsync(Hd);
        var oldPublisher = await ConnectMediaAsync(_ana, _anaUser.Id);
        var viewer = await ConnectMediaAsync(_bruno, _anaUser.Id);
        Assert.Equal(MediaContract.KeyframeRequest, (await ReceiveAsync(oldPublisher))![0]);
        await SendAsync(oldPublisher, Packet(MediaContract.Video, keyframe: true, 1));
        Assert.Equal(MediaContract.Video, (await ReceiveAsync(viewer))![0]);

        // Mesma conexão, novo anúncio com outra configuração, e um canal de mídia novo.
        var sd = new ScreenShareStart(960, 540, 60, 2500, true);
        var replaced = await ana.StartAsync(sd);
        Assert.Equal(540, replaced!.Height);
        var updated = await bruno.NextPresenceAsync(p => p.ScreenShares is { Count: 1 } shares && shares[0].Height == 540);
        Assert.Equal(2500, updated.ScreenShares![0].BitrateKbps);
        var newPublisher = await ConnectMediaAsync(_ana, _anaUser.Id);

        // O canal antigo é fechado pelo servidor; quem assiste passa a receber do novo, a partir de um quadro-chave.
        Assert.Null(await ReceiveAsync(oldPublisher));
        await SendAsync(newPublisher, Packet(MediaContract.Video, keyframe: false, 9));
        var keyframe = Packet(MediaContract.Video, keyframe: true, 4, 5, 6);
        await SendAsync(newPublisher, keyframe);
        Assert.Equal(keyframe, await ReceiveAsync(viewer));

        // O fechamento do canal antigo não encerrou o compartilhamento.
        var presence = await bruno.Connection.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, _group.Id);
        Assert.Equal(540, Assert.Single(presence!.ScreenShares!).Height);
    }

    private sealed class Hub
    {
        private readonly Channel<GroupPresenceDto> _presence = Channel.CreateUnbounded<GroupPresenceDto>();

        public Hub(HubConnection connection)
        {
            Connection = connection;
            connection.On<GroupPresenceDto>(GroupsHubContract.PresenceChanged, p => _presence.Writer.TryWrite(p));
        }

        public HubConnection Connection { get; }

        public Task<GroupPresenceDto?> JoinAsync(Guid groupId) =>
            Connection.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, groupId);

        public Task<ScreenShareDto?> StartAsync(ScreenShareStart start) =>
            Connection.InvokeAsync<ScreenShareDto?>(GroupsHubContract.StartScreenShare, start);

        public async Task<GroupPresenceDto> NextPresenceAsync(Func<GroupPresenceDto, bool> match)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (true)
            {
                var presence = await _presence.Reader.ReadAsync(timeout.Token);
                if (match(presence))
                {
                    return presence;
                }
            }
        }
    }

    private sealed class SocketDisposer(WebSocket socket) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            socket.Abort();
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
