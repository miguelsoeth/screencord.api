using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenCord.Api.Versioning;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace ScreenCord.Api.Tests;

public sealed class ClientVersionTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient CreateClient(string minVersion, string? appVersion)
    {
        var client = _factory.WithWebHostBuilder(builder => builder.UseSetting("Clients:MinVersion", minVersion)).CreateClient();
        if (appVersion is not null)
        {
            client.DefaultRequestHeaders.Add(ClientVersionContract.Header, appVersion);
        }

        return client;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/auth/discord", new DiscordLoginRequest("token-ana"));

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.3.1")]
    [InlineData("1.0.0")]
    [InlineData("0.0.0-dev")]
    public async Task VersaoIgualOuAcimaDaMinima_EAtendida(string appVersion)
    {
        var response = await LoginAsync(CreateClient("0.2.0", appVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("0.1.9")]
    [InlineData("0.1.0")]
    [InlineData(null)]
    public async Task VersaoAbaixoDaMinima_Recebe426ComOCodigoDeAtualizacao(string? appVersion)
    {
        var response = await LoginAsync(CreateClient("0.2.0", appVersion));

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal(ClientVersionContract.OutdatedCode, error!.Code);
        // A v0.1.0 só mostra textos de até 300 caracteres.
        Assert.InRange(error.Message.Length, 1, 300);
        Assert.Contains(ClientVersionContract.DownloadPage, error.Message);
    }

    [Fact]
    public async Task SemCabecalho_ContaComoV010_AceitaEnquantoElaForAMinima()
    {
        var response = await LoginAsync(CreateClient("0.1.0", appVersion: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VersaoMalFormada_Recebe400()
    {
        var response = await LoginAsync(CreateClient("0.1.0", "abc"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HealthMostraAVersaoMinima_SemExigirOCabecalho()
    {
        var response = await CreateClient("0.5.0", "0.1.0").GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("0.5.0", body.RootElement.GetProperty("minClientVersion").GetString());
    }

    [Fact]
    public async Task HubTambemRecusaVersaoAntiga()
    {
        var response = await CreateClient("0.2.0", "0.1.0").PostAsync(GroupsHubContract.Path + "/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Theory]
    [InlineData("0.2.0", 0, 2, 0, false)]
    [InlineData("v1.4.7", 1, 4, 7, false)]
    [InlineData("0.3.0+abc123", 0, 3, 0, false)]
    [InlineData("0.0.0-dev", 0, 0, 0, true)]
    [InlineData("0.4.0-beta", 0, 4, 0, false)]
    public void InterpretaVersoes(string text, int major, int minor, int patch, bool devBuild)
    {
        Assert.True(ClientVersionGate.TryParse(text, out var version, out var isDevBuild));
        Assert.Equal(new Version(major, minor, patch), version);
        Assert.Equal(devBuild, isDevBuild);
    }

    [Fact]
    public void VersaoMinimaInvalida_ImpedeASubidaDaApi()
    {
        Assert.Throws<InvalidOperationException>(() => ClientVersionGate.ParseMinVersion("0.2"));
        Assert.Throws<InvalidOperationException>(() => ClientVersionGate.ParseMinVersion("0.0.0-dev"));
    }
}
