using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ScreenCord.Api.Tests;

public sealed class GroupTests : IAsyncLifetime
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    private readonly ApiFactory _factory = new();
    private readonly List<HubConnection> _connections = [];
    private HttpClient _ana = null!;
    private HttpClient _bruno = null!;
    private HttpClient _carla = null!;
    private UserDto _anaUser = null!;
    private UserDto _brunoUser = null!;
    private UserDto _carlaUser = null!;

    public async Task InitializeAsync()
    {
        (_ana, _anaUser) = await _factory.LoginAsync("token-ana");
        (_bruno, _brunoUser) = await _factory.LoginAsync("token-bruno");
        (_carla, _carlaUser) = await _factory.LoginAsync("token-carla");
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        _factory.Dispose();
    }

    private static async Task MakeFriendsAsync(HttpClient a, UserDto aUser, HttpClient b, UserDto bUser)
    {
        (await a.PostAsJsonAsync("/friends/requests", new FriendRequestCreate(bUser.Id))).EnsureSuccessStatusCode();
        (await b.PostAsync($"/friends/requests/{aUser.Id}/accept", null)).EnsureSuccessStatusCode();
    }

    private static async Task<GroupDto> CreateGroupAsync(HttpClient client, string name = "Amigos")
    {
        var response = await client.PostAsJsonAsync("/groups", new GroupCreate(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GroupDto>())!;
    }

    private static Task<HttpResponseMessage> AddMemberAsync(HttpClient client, Guid groupId, UserDto user) =>
        client.PostAsJsonAsync($"/groups/{groupId}/members", new GroupMemberCreate(user.Id));

    private static async Task<List<GroupDto>> GroupsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<GroupDto>>("/groups"))!;

    private static async Task<GroupDetailsDto> DetailsAsync(HttpClient client, Guid groupId) =>
        (await client.GetFromJsonAsync<GroupDetailsDto>($"/groups/{groupId}"))!;

    /// <summary>Conexão real com o hub (WebSocket sobre o servidor de testes), autenticada pelo cabeçalho.</summary>
    private async Task<HubClient> ConnectAsync(HttpClient client)
    {
        var token = client.DefaultRequestHeaders.Authorization?.Parameter;
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/groups"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token);
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var webSocket = server.CreateWebSocketClient();
                    webSocket.ConfigureRequest = request =>
                    {
                        if (token is not null)
                        {
                            request.Headers.Authorization = "Bearer " + token;
                        }
                    };
                    return await webSocket.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        _connections.Add(connection);

        var hub = new HubClient(connection);
        await connection.StartAsync();
        return hub;
    }

    private sealed class HubClient
    {
        private readonly Channel<GroupPresenceDto> _presence = Channel.CreateUnbounded<GroupPresenceDto>();
        private readonly Channel<GroupDto> _groups = Channel.CreateUnbounded<GroupDto>();
        private readonly Channel<Guid> _removed = Channel.CreateUnbounded<Guid>();
        private readonly Channel<bool> _friendsChanged = Channel.CreateUnbounded<bool>();

        public HubClient(HubConnection connection)
        {
            Connection = connection;
            connection.On<GroupPresenceDto>(GroupsHubContract.PresenceChanged, p => _presence.Writer.TryWrite(p));
            connection.On<GroupDto>(GroupsHubContract.GroupChanged, g => _groups.Writer.TryWrite(g));
            connection.On<Guid>(GroupsHubContract.GroupRemoved, id => _removed.Writer.TryWrite(id));
            connection.On(GroupsHubContract.FriendsChanged, () => _friendsChanged.Writer.TryWrite(true));
        }

        public HubConnection Connection { get; }

        public Task<GroupPresenceDto?> JoinAsync(Guid groupId) =>
            Connection.InvokeAsync<GroupPresenceDto?>(GroupsHubContract.JoinGroup, groupId);

        /// <summary>Espera a próxima presença do grupo que satisfaça a condição.</summary>
        public Task<GroupPresenceDto> NextPresenceAsync(Guid groupId, Func<GroupPresenceDto, bool> match) =>
            NextAsync(_presence, p => p.GroupId == groupId && match(p));

        public Task<GroupDto> NextGroupAsync(Func<GroupDto, bool> match) => NextAsync(_groups, match);

        public Task<Guid> NextRemovedAsync() => NextAsync(_removed, _ => true);

        public Task NextFriendsChangedAsync() => NextAsync(_friendsChanged, _ => true);

        private static async Task<T> NextAsync<T>(Channel<T> channel, Func<T, bool> match)
        {
            using var timeout = new CancellationTokenSource(EventTimeout);
            while (true)
            {
                var item = await channel.Reader.ReadAsync(timeout.Token);
                if (match(item))
                {
                    return item;
                }
            }
        }
    }

    [Fact]
    public async Task Criar_RetornaGrupoComOCriadorComoIntegrante()
    {
        var group = await CreateGroupAsync(_ana, "  Jogos   de sexta ");

        Assert.Equal("Jogos de sexta", group.Name);
        Assert.Equal(1, group.MemberCount);
        Assert.Empty(group.ConnectedUserIds);
        Assert.Equal(group.Id, Assert.Single(await GroupsAsync(_ana)).Id);
        Assert.Empty(await GroupsAsync(_bruno));

        var details = await DetailsAsync(_ana, group.Id);
        Assert.Equal(_anaUser.Id, Assert.Single(details.Members).Id);
    }

    [Fact]
    public async Task Criar_AlemDoLimitePorConta_Retorna409()
    {
        for (var i = 0; i < GroupsHubContract.MaxCreatedGroups; i++)
        {
            await CreateGroupAsync(_ana, $"Grupo {i}");
        }

        var response = await _ana.PostAsJsonAsync("/groups", new GroupCreate("Mais um"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("group_limit", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code);

        // O limite é por conta: outra pessoa ainda cria.
        await CreateGroupAsync(_bruno, "Do Bruno");
    }

    [Fact]
    public async Task Criar_SairDeUmGrupoCriadoLiberaEspaco()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var groups = new List<GroupDto>();
        for (var i = 0; i < GroupsHubContract.MaxCreatedGroups; i++)
        {
            groups.Add(await CreateGroupAsync(_ana, $"Grupo {i}"));
        }

        (await AddMemberAsync(_ana, groups[0].Id, _brunoUser)).EnsureSuccessStatusCode();
        (await _ana.DeleteAsync($"/groups/{groups[0].Id}/members/me")).EnsureSuccessStatusCode();

        await CreateGroupAsync(_ana, "Outro");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012345678901")]
    public async Task Criar_NomeInvalido_Retorna400(string name)
    {
        var response = await _ana.PostAsJsonAsync("/groups", new GroupCreate(name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdicionarAmigo_AumentaOGrupoEOAmigoPassaAVerOGrupo()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);

        var response = await AddMemberAsync(_ana, group.Id, _brunoUser);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<GroupDto>())!.MemberCount);
        var seen = Assert.Single(await GroupsAsync(_bruno));
        Assert.Equal(2, seen.MemberCount);
    }

    [Fact]
    public async Task QualquerIntegrante_PodeAdicionarOsProprioAmigos()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        await MakeFriendsAsync(_bruno, _brunoUser, _carla, _carlaUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();

        // Carla não é amiga da Ana, mas é do Bruno, que já está no grupo.
        Assert.Equal(HttpStatusCode.BadRequest, (await AddMemberAsync(_ana, group.Id, _carlaUser)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddMemberAsync(_bruno, group.Id, _carlaUser)).StatusCode);

        var details = await DetailsAsync(_carla, group.Id);
        Assert.Equal(3, details.Members.Count);
    }

    [Fact]
    public async Task Adicionar_JaIntegrante_Retorna409()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();

        var response = await AddMemberAsync(_ana, group.Id, _brunoUser);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_member", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code);
    }

    [Fact]
    public async Task QuemNaoEIntegrante_NaoVeNemAlteraOGrupo()
    {
        await MakeFriendsAsync(_carla, _carlaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);

        Assert.Equal(HttpStatusCode.NotFound, (await _carla.GetAsync($"/groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddMemberAsync(_carla, group.Id, _brunoUser)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _carla.DeleteAsync($"/groups/{group.Id}/members/me")).StatusCode);
    }

    [Fact]
    public async Task Sair_UltimoIntegrante_ApagaOGrupo()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await _ana.DeleteAsync($"/groups/{group.Id}/members/me")).StatusCode);
        Assert.Empty(await GroupsAsync(_ana));
        Assert.Equal(1, Assert.Single(await GroupsAsync(_bruno)).MemberCount);

        Assert.Equal(HttpStatusCode.NoContent, (await _bruno.DeleteAsync($"/groups/{group.Id}/members/me")).StatusCode);
        Assert.Empty(await GroupsAsync(_bruno));
    }

    [Fact]
    public async Task Rotas_ExigemLogin()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/groups", new GroupCreate("x"))).StatusCode);
    }

    [Fact]
    public async Task Hub_SemToken_EhRecusado()
    {
        var anonymous = _factory.CreateClient();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(anonymous));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task Entrar_AtualizaAPresencaParaTodosOsIntegrantesEmTempoReal()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();
        var ana = await ConnectAsync(_ana);
        var bruno = await ConnectAsync(_bruno);

        var joined = await ana.JoinAsync(group.Id);

        Assert.Equal([_anaUser.Id], joined!.ConnectedUserIds);
        var seenByBruno = await bruno.NextPresenceAsync(group.Id, p => p.ConnectedUserIds.Contains(_anaUser.Id));
        Assert.Single(seenByBruno.ConnectedUserIds);

        // Os dois ao mesmo tempo na sala.
        await bruno.JoinAsync(group.Id);
        var both = await ana.NextPresenceAsync(group.Id, p => p.ConnectedUserIds.Count == 2);
        Assert.Contains(_brunoUser.Id, both.ConnectedUserIds);
        Assert.True(both.Version > seenByBruno.Version);

        var details = await DetailsAsync(_ana, group.Id);
        Assert.All(details.Members, m => Assert.True(m.IsConnected));
        Assert.Equal(2, Assert.Single(await GroupsAsync(_bruno)).ConnectedUserIds.Count);
    }

    [Fact]
    public async Task EntrarEmOutroGrupo_SaiDoAnterior()
    {
        var first = await CreateGroupAsync(_ana, "Primeiro");
        var second = await CreateGroupAsync(_ana, "Segundo");
        var ana = await ConnectAsync(_ana);
        await ana.JoinAsync(first.Id);

        await ana.JoinAsync(second.Id);

        await ana.NextPresenceAsync(first.Id, p => p.ConnectedUserIds.Count == 0);
        var groups = await GroupsAsync(_ana);
        Assert.Empty(groups.Single(g => g.Id == first.Id).ConnectedUserIds);
        Assert.Single(groups.Single(g => g.Id == second.Id).ConnectedUserIds);
    }

    [Fact]
    public async Task Entrar_QuemNaoEIntegrante_RecebeNull()
    {
        var group = await CreateGroupAsync(_ana);
        var carla = await ConnectAsync(_carla);

        Assert.Null(await carla.JoinAsync(group.Id));
        Assert.Empty(Assert.Single(await GroupsAsync(_ana)).ConnectedUserIds);
    }

    [Fact]
    public async Task ConexaoEncerrada_SaiDaSalaEAvisaOsOutros()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();
        var ana = await ConnectAsync(_ana);
        var bruno = await ConnectAsync(_bruno);
        await ana.JoinAsync(group.Id);
        await bruno.NextPresenceAsync(group.Id, p => p.ConnectedUserIds.Count == 1);

        await ana.Connection.StopAsync();

        await bruno.NextPresenceAsync(group.Id, p => p.ConnectedUserIds.Count == 0);
    }

    [Fact]
    public async Task LeaveGroup_SaiDaSala()
    {
        var group = await CreateGroupAsync(_ana);
        var ana = await ConnectAsync(_ana);
        await ana.JoinAsync(group.Id);

        await ana.Connection.InvokeAsync(GroupsHubContract.LeaveGroup);

        await ana.NextPresenceAsync(group.Id, p => p.ConnectedUserIds.Count == 0);
    }

    [Fact]
    public async Task NovoIntegrante_TodosRecebemOGrupoAtualizado()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        var ana = await ConnectAsync(_ana);
        var bruno = await ConnectAsync(_bruno);

        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();

        Assert.Equal(2, (await bruno.NextGroupAsync(g => g.Id == group.Id)).MemberCount);
        Assert.Equal(2, (await ana.NextGroupAsync(g => g.Id == group.Id && g.MemberCount == 2)).MemberCount);
    }

    [Fact]
    public async Task Amizade_CadaMudancaAvisaOsDoisLadosPeloHub()
    {
        var ana = await ConnectAsync(_ana);
        var bruno = await ConnectAsync(_bruno);

        (await _ana.PostAsJsonAsync("/friends/requests", new FriendRequestCreate(_brunoUser.Id))).EnsureSuccessStatusCode();
        await Task.WhenAll(ana.NextFriendsChangedAsync(), bruno.NextFriendsChangedAsync());

        (await _bruno.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null)).EnsureSuccessStatusCode();
        await Task.WhenAll(ana.NextFriendsChangedAsync(), bruno.NextFriendsChangedAsync());

        (await _bruno.DeleteAsync($"/friends/{_anaUser.Id}")).EnsureSuccessStatusCode();
        await Task.WhenAll(ana.NextFriendsChangedAsync(), bruno.NextFriendsChangedAsync());

        (await _ana.PostAsJsonAsync("/friends/requests", new FriendRequestCreate(_brunoUser.Id))).EnsureSuccessStatusCode();
        await Task.WhenAll(ana.NextFriendsChangedAsync(), bruno.NextFriendsChangedAsync());

        (await _bruno.DeleteAsync($"/friends/requests/{_anaUser.Id}")).EnsureSuccessStatusCode();
        await Task.WhenAll(ana.NextFriendsChangedAsync(), bruno.NextFriendsChangedAsync());
    }

    [Fact]
    public async Task SairDoGrupo_ConectadoNaSala_TiraDaPresencaEAvisaAsOutrasJanelas()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();
        var ana = await ConnectAsync(_ana);
        var bruno = await ConnectAsync(_bruno);
        await bruno.JoinAsync(group.Id);
        await ana.NextPresenceAsync(group.Id, p => p.ConnectedUserIds.Contains(_brunoUser.Id));

        (await _bruno.DeleteAsync($"/groups/{group.Id}/members/me")).EnsureSuccessStatusCode();

        Assert.Equal(group.Id, await bruno.NextRemovedAsync());
        var updated = await ana.NextGroupAsync(g => g.Id == group.Id && g.MemberCount == 1);
        Assert.Empty(updated.ConnectedUserIds);
    }

    [Fact]
    public async Task Excluir_PeloCriador_TiraDeTodosEAvisaPeloHub()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();
        var bruno = await ConnectAsync(_bruno);
        await bruno.JoinAsync(group.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await _ana.DeleteAsync($"/groups/{group.Id}")).StatusCode);

        Assert.Equal(group.Id, await bruno.NextRemovedAsync());
        Assert.Empty(await GroupsAsync(_ana));
        Assert.Empty(await GroupsAsync(_bruno));
        Assert.Equal(HttpStatusCode.NotFound, (await _ana.GetAsync($"/groups/{group.Id}")).StatusCode);

        // Quem estava na sala já não está mais: entrar de novo é recusado.
        Assert.Null(await bruno.JoinAsync(group.Id));
    }

    [Fact]
    public async Task Excluir_SoQuemCriou()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();

        Assert.True((await _ana.GetFromJsonAsync<GroupDetailsDto>($"/groups/{group.Id}"))!.CanDelete);
        Assert.False((await _bruno.GetFromJsonAsync<GroupDetailsDto>($"/groups/{group.Id}"))!.CanDelete);

        var refused = await _bruno.DeleteAsync($"/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("not_group_owner", (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Code);
        Assert.Equal(HttpStatusCode.NotFound, (await _carla.DeleteAsync($"/groups/{group.Id}")).StatusCode);
        Assert.Equal(2, Assert.Single(await GroupsAsync(_ana)).MemberCount);
    }

    [Fact]
    public async Task Detalhes_MarcamQuemCriouOGrupo()
    {
        await MakeFriendsAsync(_ana, _anaUser, _bruno, _brunoUser);
        var group = await CreateGroupAsync(_ana);
        (await AddMemberAsync(_ana, group.Id, _brunoUser)).EnsureSuccessStatusCode();

        var members = (await _bruno.GetFromJsonAsync<GroupDetailsDto>($"/groups/{group.Id}"))!.Members;
        Assert.True(Assert.Single(members, m => m.Id == _anaUser.Id).IsOwner);
        Assert.False(Assert.Single(members, m => m.Id == _brunoUser.Id).IsOwner);
    }
}
