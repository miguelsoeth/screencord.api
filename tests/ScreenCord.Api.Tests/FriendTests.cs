using System.Net;
using System.Net.Http.Json;
using ScreenCord.Contracts;

namespace ScreenCord.Api.Tests;

public sealed class FriendTests : IAsyncLifetime
{
    private readonly ApiFactory _factory = new();
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

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static Task<HttpResponseMessage> RequestAsync(HttpClient from, UserDto to) =>
        from.PostAsJsonAsync("/friends/requests", new FriendRequestCreate(to.Id));

    private static async Task<List<UserDto>> FriendsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<UserDto>>("/friends"))!;

    private static async Task<FriendRequestsResponse> RequestsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<FriendRequestsResponse>("/friends/requests"))!;

    private static async Task<List<UserDto>> SearchAsync(HttpClient client, string q) =>
        (await client.GetFromJsonAsync<List<UserDto>>("/users/search?q=" + Uri.EscapeDataString(q)))!;

    [Theory]
    [InlineData("bruno")]
    [InlineData("@bruno")]
    [InlineData("BRU")]
    [InlineData("run")]
    public async Task Busca_EncontraPorNomeDeUsuario(string term)
    {
        var results = await SearchAsync(_ana, term);

        var found = Assert.Single(results);
        Assert.Equal(_brunoUser.Id, found.Id);
        Assert.Equal(RelationshipStatus.None, found.Relationship);
    }

    [Fact]
    public async Task Busca_EncontraPorNomeDeExibicao_ENaoRetornaOProprioUsuario()
    {
        Assert.Equal(_carlaUser.Id, Assert.Single(await SearchAsync(_ana, "Carla")).Id);
        Assert.Empty(await SearchAsync(_ana, "ana"));
    }

    [Fact]
    public async Task Busca_TrataCuringasComoTextoLiteral()
    {
        Assert.Empty(await SearchAsync(_ana, "%%"));
        Assert.Equal(_carlaUser.Id, Assert.Single(await SearchAsync(_ana, "a_d")).Id);
        Assert.Empty(await SearchAsync(_ana, "b_u"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("@a")]
    public async Task Busca_TermoCurto_Retorna400(string term)
    {
        var response = await _ana.GetAsync("/users/search?q=" + Uri.EscapeDataString(term));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Solicitacao_FicaPendenteParaOsDoisLados()
    {
        var response = await RequestAsync(_ana, _brunoUser);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(_brunoUser.Id, Assert.Single((await RequestsAsync(_ana)).Outgoing).Id);
        Assert.Equal(_anaUser.Id, Assert.Single((await RequestsAsync(_bruno)).Incoming).Id);
        Assert.Empty(await FriendsAsync(_ana));
        Assert.Empty(await FriendsAsync(_bruno));
        Assert.Equal(RelationshipStatus.PendingOutgoing, Assert.Single(await SearchAsync(_ana, "bruno")).Relationship);
        Assert.Equal(RelationshipStatus.PendingIncoming, Assert.Single(await SearchAsync(_bruno, "ana.silva")).Relationship);
    }

    [Fact]
    public async Task Aceitar_TornaOsDoisAmigos()
    {
        await RequestAsync(_ana, _brunoUser);

        var accept = await _bruno.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null);

        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
        Assert.Equal(_brunoUser.Id, Assert.Single(await FriendsAsync(_ana)).Id);
        Assert.Equal(_anaUser.Id, Assert.Single(await FriendsAsync(_bruno)).Id);
        Assert.Empty((await RequestsAsync(_bruno)).Incoming);
        Assert.Equal(RelationshipStatus.Friend, Assert.Single(await SearchAsync(_ana, "bruno")).Relationship);
        Assert.Empty(await FriendsAsync(_carla));
    }

    [Fact]
    public async Task QuemEnviouNaoPodeAceitarAPropriaSolicitacao()
    {
        await RequestAsync(_ana, _brunoUser);

        var accept = await _ana.PostAsync($"/friends/requests/{_brunoUser.Id}/accept", null);

        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        Assert.Empty(await FriendsAsync(_ana));
    }

    [Fact]
    public async Task TerceiroNaoPodeAceitarSolicitacaoAlheia()
    {
        await RequestAsync(_ana, _brunoUser);

        var accept = await _carla.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null);

        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        Assert.Empty(await FriendsAsync(_bruno));
    }

    [Fact]
    public async Task SolicitacoesCruzadas_ConfirmamAAmizade()
    {
        await RequestAsync(_ana, _brunoUser);

        var response = await RequestAsync(_bruno, _anaUser);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RelationshipStatus.Friend, (await response.Content.ReadFromJsonAsync<UserDto>())!.Relationship);
        Assert.Single(await FriendsAsync(_ana));
    }

    [Fact]
    public async Task SolicitacaoRepetida_EhIdempotente()
    {
        await RequestAsync(_ana, _brunoUser);

        var again = await RequestAsync(_ana, _brunoUser);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single((await RequestsAsync(_bruno)).Incoming);
    }

    [Fact]
    public async Task SolicitacaoParaAmigo_Retorna409()
    {
        await RequestAsync(_ana, _brunoUser);
        await _bruno.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null);

        var response = await RequestAsync(_ana, _brunoUser);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("already_friends", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code);
    }

    [Fact]
    public async Task SolicitacaoParaSiMesmo_Retorna400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await RequestAsync(_ana, _anaUser)).StatusCode);
    }

    [Fact]
    public async Task SolicitacaoParaUsuarioInexistente_Retorna404()
    {
        var response = await _ana.PostAsJsonAsync("/friends/requests", new FriendRequestCreate(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Recusar_RemoveASolicitacao()
    {
        await RequestAsync(_ana, _brunoUser);

        var decline = await _bruno.DeleteAsync($"/friends/requests/{_anaUser.Id}");

        Assert.Equal(HttpStatusCode.NoContent, decline.StatusCode);
        Assert.Empty((await RequestsAsync(_ana)).Outgoing);
        Assert.Empty(await FriendsAsync(_ana));
        Assert.Equal(RelationshipStatus.None, Assert.Single(await SearchAsync(_ana, "bruno")).Relationship);
    }

    [Fact]
    public async Task Cancelar_RemoveASolicitacaoEnviada()
    {
        await RequestAsync(_ana, _brunoUser);

        var cancel = await _ana.DeleteAsync($"/friends/requests/{_brunoUser.Id}");

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Empty((await RequestsAsync(_bruno)).Incoming);
    }

    [Fact]
    public async Task ExcluirSolicitacao_NaoDesfazAmizade()
    {
        await RequestAsync(_ana, _brunoUser);
        await _bruno.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null);

        var response = await _ana.DeleteAsync($"/friends/requests/{_brunoUser.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await FriendsAsync(_ana));
    }

    [Fact]
    public async Task RemoverAmigo_DesfazParaOsDoisLados()
    {
        await RequestAsync(_ana, _brunoUser);
        await _bruno.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null);

        var remove = await _bruno.DeleteAsync($"/friends/{_anaUser.Id}");

        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Empty(await FriendsAsync(_ana));
        Assert.Empty(await FriendsAsync(_bruno));
    }

    [Fact]
    public async Task RemoverQuemNaoEhAmigo_Retorna404()
    {
        await RequestAsync(_ana, _brunoUser);

        Assert.Equal(HttpStatusCode.NotFound, (await _ana.DeleteAsync($"/friends/{_brunoUser.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ana.DeleteAsync($"/friends/{_carlaUser.Id}")).StatusCode);
    }

    [Fact]
    public async Task ListaDeAmigos_VemOrdenadaPorNomeDeUsuario()
    {
        await RequestAsync(_ana, _carlaUser);
        await RequestAsync(_ana, _brunoUser);
        await _carla.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null);
        await _bruno.PostAsync($"/friends/requests/{_anaUser.Id}/accept", null);

        var friends = await FriendsAsync(_ana);

        Assert.Equal(["bruno", "carla_dev"], friends.Select(f => f.Username));
        Assert.All(friends, f => Assert.Equal(RelationshipStatus.Friend, f.Relationship));
    }
}
