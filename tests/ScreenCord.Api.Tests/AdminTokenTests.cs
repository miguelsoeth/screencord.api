using System.Net;
using ScreenCord.Api.Observability;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ScreenCord.Api.Tests;

/// <summary>Token do painel: tamanho mínimo fora do desenvolvimento e bloqueio por endereço depois de erros.</summary>
public sealed class AdminTokenTests
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Production", "dev")]
    [InlineData("Production", "curto-demais")]
    [InlineData("Staging", "12345678901234567890123456789")]
    public void TokenCurto_ForaDoDesenvolvimento_ImpedeASubida(string environment, string token) =>
        Assert.Throws<InvalidOperationException>(() => ObservabilitySetup.EnsureStrongDashboardToken(token, new Env(environment)));

    [Theory]
    [InlineData("Production", "")]
    [InlineData("Production", "0123456789abcdef0123456789abcdef")]
    [InlineData("Development", "dev")]
    [InlineData("Testing", "token-do-painel")]
    public void TokenForteVazioOuEmDesenvolvimento_Passa(string environment, string token) =>
        ObservabilitySetup.EnsureStrongDashboardToken(token, new Env(environment));

    [Fact]
    public void Guard_BloqueiaDepoisDeMuitosErros_ESoDaquelaRede()
    {
        var guard = new AdminAuthGuard(new ManualTime(DateTimeOffset.UnixEpoch));
        var attacker = IPAddress.Parse("203.0.113.9");

        for (var i = 0; i < AdminAuthGuard.MaxFailures - 1; i++)
        {
            Assert.False(guard.RecordFailure(attacker));
        }

        Assert.False(guard.IsBlocked(attacker));
        Assert.True(guard.RecordFailure(attacker));
        Assert.True(guard.IsBlocked(attacker));
        Assert.False(guard.IsBlocked(IPAddress.Parse("203.0.113.10")));
    }

    [Fact]
    public void Guard_LiberaDepoisDoBloqueio_EAcertoZeraAContagem()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var guard = new AdminAuthGuard(time);
        var address = IPAddress.Parse("203.0.113.9");

        for (var i = 0; i < AdminAuthGuard.MaxFailures; i++)
        {
            guard.RecordFailure(address);
        }

        Assert.True(guard.IsBlocked(address));
        time.Advance(AdminAuthGuard.BlockDuration + TimeSpan.FromSeconds(1));
        Assert.False(guard.IsBlocked(address));

        for (var i = 0; i < AdminAuthGuard.MaxFailures - 1; i++)
        {
            guard.RecordFailure(address);
        }

        guard.RecordSuccess(address);
        Assert.False(guard.RecordFailure(address));
    }

    [Fact]
    public async Task Painel_BloqueiaOEnderecoDepoisDeTokensErrados_MesmoComOTokenCerto()
    {
        const string token = "token-do-painel";
        using var factory = new ApiFactory(new Dictionary<string, string?> { ["Observability:DashboardToken"] = token });
        var wrong = factory.CreateClient();
        wrong.DefaultRequestHeaders.Add(ObservabilityEndpoints.TokenHeader, "chute");

        for (var i = 0; i < AdminAuthGuard.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/admin/api/summary")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await wrong.GetAsync("/admin/api/summary")).StatusCode);

        var right = factory.CreateClient();
        right.DefaultRequestHeaders.Add(ObservabilityEndpoints.TokenHeader, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await right.GetAsync("/admin/api/summary")).StatusCode);
    }
}
