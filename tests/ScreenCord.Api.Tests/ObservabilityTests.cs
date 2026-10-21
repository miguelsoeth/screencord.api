using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenCord.Api.Data;
using ScreenCord.Api.Observability;
using ScreenCord.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Api.Tests;

/// <summary>Relatórios de erro do aplicativo, erros da própria API, métricas e o painel em /admin.</summary>
public sealed class ObservabilityTests : IDisposable
{
    private const string Token = "token-do-painel";

    private readonly ApiFactory _factory = new(new Dictionary<string, string?> { ["Observability:DashboardToken"] = Token });

    public void Dispose() => _factory.Dispose();

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ObservabilityEndpoints.TokenHeader, Token);
        return client;
    }

    private static ClientErrorReport Report(params ClientLogEntry[] entries) =>
        new("0.6.0", "Windows 11", "Production", Guid.NewGuid(), Guid.NewGuid(), entries);

    private static ClientLogEntry Entry(string message = "Falha ao entrar no grupo.", string? exception = null, string? traceId = null) =>
        new(DateTimeOffset.UtcNow, "Error", "ScreenCord.ViewModels.GroupsViewModel", message, exception, traceId);

    [Fact]
    public async Task RelatorioDoApp_SemLogin_ApareceNoPainelAgrupado()
    {
        var client = _factory.CreateClient();
        const string exception = "System.InvalidOperationException: quebrou\r\n   at ScreenCord.A.B() in C:\\src\\A.cs:line 10";

        var first = await client.PostAsJsonAsync(TelemetryContract.ErrorsPath, Report(Entry(exception: exception), Entry(exception: exception)));
        var second = await client.PostAsJsonAsync(TelemetryContract.ErrorsPath, Report(Entry("Outra coisa 42")));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var issues = await Admin().GetFromJsonAsync<JsonElement>("/admin/api/issues?source=app");
        Assert.Equal(2, issues.GetArrayLength());
        var repeated = issues.EnumerateArray().Single(i => i.GetProperty("count").GetInt32() == 2);
        Assert.Equal(1, repeated.GetProperty("affected").GetInt32());
        Assert.Equal("0.6.0", repeated.GetProperty("versions")[0].GetString());

        var events = await Admin().GetFromJsonAsync<JsonElement>($"/admin/api/events?fingerprint={repeated.GetProperty("fingerprint").GetString()}");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal(exception, events[0].GetProperty("exception").GetString());
        Assert.Equal("Windows 11", events[0].GetProperty("os").GetString());
    }

    [Fact]
    public async Task RelatorioDoApp_LevaComputadorEstadoELogDeAntes_ParaCadaOcorrencia()
    {
        var device = new Dictionary<string, string> { ["gpu0"] = "NVIDIA GeForce RTX 3050 (driver 32.0.16.1088, 6001 MB)" };
        var context = Enumerable.Range(0, 40).ToDictionary(i => $"chave{i:00}", i => i == 0 ? new string('v', 500) : "x");
        var breadcrumbs = Enumerable.Range(0, 45).Select(i => $"01:23:{i:00}.000 INF ScreenShareService: linha {i}\r\nquebrada").ToList();
        var entry = Entry() with { Context = context, Breadcrumbs = breadcrumbs };

        var response = await _factory.CreateClient().PostAsJsonAsync(TelemetryContract.ErrorsPath, Report(entry) with { Device = device });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single((await Admin().GetFromJsonAsync<JsonElement>("/admin/api/events")).EnumerateArray());
        var savedDevice = JsonSerializer.Deserialize<Dictionary<string, string>>(stored.GetProperty("device").GetString()!)!;
        Assert.Equal(device["gpu0"], savedDevice["gpu0"]);
        var savedContext = JsonSerializer.Deserialize<Dictionary<string, string>>(stored.GetProperty("context").GetString()!)!;
        Assert.Equal(TelemetryContract.MaxContextEntries, savedContext.Count);
        Assert.True(savedContext["chave00"].Length <= 301);

        // Ficam as mais novas, uma por linha.
        var lines = stored.GetProperty("breadcrumbs").GetString()!.Split('\n');
        Assert.Equal(TelemetryContract.MaxBreadcrumbs, lines.Length);
        Assert.Equal("01:23:44.000 INF ScreenShareService: linha 44 quebrada", lines[^1]);
    }

    [Fact]
    public async Task RelatorioDeVersaoAntiga_SemOsCamposNovos_ContinuaAceito()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(TelemetryContract.ErrorsPath, Report(Entry()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single((await Admin().GetFromJsonAsync<JsonElement>("/admin/api/events")).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("device").ValueKind);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("breadcrumbs").ValueKind);
    }

    [Fact]
    public async Task RelatorioDoApp_InvalidoOuGrande_ERecusadoOuCortado()
    {
        var client = _factory.CreateClient();

        var empty = await client.PostAsJsonAsync(TelemetryContract.ErrorsPath, Report());
        var badVersion = await client.PostAsJsonAsync(TelemetryContract.ErrorsPath, Report(Entry()) with { AppVersion = "abc" });
        var many = await client.PostAsJsonAsync(TelemetryContract.ErrorsPath,
            Report(Enumerable.Range(0, 80).Select(_ => Entry(new string('x', 10_000), traceId: "não-é-um-trace")).ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badVersion.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, many.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<AppDbContext>().ErrorEvents.ToList();
        Assert.Equal(TelemetryContract.MaxEntries, stored.Count);
        Assert.All(stored, e => Assert.True(e.Message.Length <= ErrorEvent.MaxMessageLength + 1));
        Assert.All(stored, e => Assert.Null(e.TraceId));
    }

    [Fact]
    public async Task Painel_SemTokenCerto_NaoMostraDados()
    {
        var anonymous = _factory.CreateClient();
        var wrong = _factory.CreateClient();
        wrong.DefaultRequestHeaders.Add(ObservabilityEndpoints.TokenHeader, "chute");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/api/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/admin/api/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync("/admin/api/summary")).StatusCode);
        Assert.Contains("ScreenCord", await anonymous.GetStringAsync("/admin"));
    }

    [Fact]
    public async Task Painel_SemTokenConfigurado_NaoExiste()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ObservabilityEndpoints.TokenHeader, "");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/summary")).StatusCode);
    }

    [Fact]
    public async Task Requisicao_DevolveOTraceRecebidoDoApp_EEntraNasMetricas()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var request = new HttpRequestMessage(HttpMethod.Get, "/limits");
        request.Headers.Add("traceparent", $"00-{traceId}-00f067aa0ba902b7-01");

        var response = await _factory.CreateClient().SendAsync(request);
        await _factory.CreateClient().GetAsync("/groups");

        Assert.Equal(traceId, response.Headers.GetValues(TelemetryContract.TraceIdHeader).Single());
        var summary = await Admin().GetFromJsonAsync<JsonElement>("/admin/api/summary");
        var routes = summary.GetProperty("requests").GetProperty("routes").EnumerateArray().ToList();
        Assert.Equal(1, routes.Single(r => r.GetProperty("route").GetString() == "GET /limits").GetProperty("requests").GetInt32());
        Assert.Equal(1, routes.Single(r => r.GetProperty("route").GetString() == "GET /groups").GetProperty("clientErrors").GetInt32());
    }

    [Fact]
    public async Task ErroNoLogDaApi_ApareceNoPainel()
    {
        var logger = _factory.Services.GetRequiredService<ILogger<ObservabilityTests>>();
        logger.LogError(new InvalidOperationException("relay quebrou"), "Falha no relay da mídia.");

        var issues = default(JsonElement);
        for (var i = 0; i < 50; i++)
        {
            issues = await Admin().GetFromJsonAsync<JsonElement>("/admin/api/issues?source=api");
            if (issues.GetArrayLength() > 0)
            {
                break;
            }

            await Task.Delay(100);
        }

        var issue = Assert.Single(issues.EnumerateArray());
        Assert.Equal("Falha no relay da mídia.", issue.GetProperty("message").GetString());
        Assert.Equal("Error", issue.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Limpeza_ApagaOsAntigosEOsAcimaDoTeto()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        foreach (var age in new[] { 40, 5, 3, 2, 1 })
        {
            db.ErrorEvents.Add(new ErrorEvent
            {
                Id = Guid.NewGuid(), Source = ErrorEvent.FromApp, Level = "Error", Category = "c", Message = $"{age} dias",
                Fingerprint = "f", OccurredAtUtc = now.AddDays(-age), ReceivedAtUtc = now,
            });
        }

        await db.SaveChangesAsync();

        await ErrorSink.CleanupAsync(db, new ObservabilityOptions { RetentionDays = 30, MaxEvents = 3 }, now, CancellationToken.None);

        Assert.Equal(["1 dias", "2 dias", "3 dias"], db.ErrorEvents.OrderBy(e => e.Message).Select(e => e.Message).ToList());
    }

    [Fact]
    public async Task UsuariosAtivos_ContaCadaContaUmaVezPorDia()
    {
        var (ana, anaUser) = await _factory.LoginAsync("token-ana");
        var (bruno, _) = await _factory.LoginAsync("token-bruno");
        await ana.GetAsync("/friends");
        await ana.GetAsync("/groups");
        await bruno.GetAsync("/friends");
        await _factory.CreateClient().GetAsync("/limits");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, db.UserActivities.Count(a => a.UserId == anaUser.Id && a.Day == today));
            db.UserActivities.Add(new UserActivity { UserId = anaUser.Id, Day = today.AddDays(-3) });
            db.UserActivities.Add(new UserActivity { UserId = Guid.NewGuid(), Day = today.AddDays(-10) });
            await db.SaveChangesAsync();
        }

        var active = (await Admin().GetFromJsonAsync<JsonElement>("/admin/api/summary")).GetProperty("activeUsers");

        Assert.Equal(2, active.GetProperty("today").GetInt32());
        Assert.Equal(2, active.GetProperty("last7Days").GetInt32());
        Assert.Equal(3, active.GetProperty("last30Days").GetInt32());
        Assert.Equal(2, active.GetProperty("accounts").GetInt32());
        var daily = active.GetProperty("daily").EnumerateArray().ToList();
        Assert.Equal(3, daily.Count);
        Assert.Equal(today.ToString("yyyy-MM-dd"), daily[^1].GetProperty("day").GetString());
        Assert.Equal(2, daily[^1].GetProperty("users").GetInt32());
    }

    [Fact]
    public async Task Limpeza_ApagaOsDiasDeUsoAntigos()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var user = Guid.NewGuid();
        db.UserActivities.AddRange(
            new UserActivity { UserId = user, Day = today.AddDays(-ActiveUsers.RetentionDays - 1) },
            new UserActivity { UserId = user, Day = today.AddDays(-ActiveUsers.RetentionDays) });
        await db.SaveChangesAsync();

        await ErrorSink.CleanupAsync(db, new ObservabilityOptions(), now, CancellationToken.None);

        Assert.Equal([today.AddDays(-ActiveUsers.RetentionDays)], db.UserActivities.Select(a => a.Day).ToList());
    }

    [Fact]
    public void Fingerprint_IgnoraLinhaDoArquivoENumerosDaMensagem()
    {
        var a = ErrorFingerprint.Compute("app", "c", "x", "System.Exception: a\n   at A.B() in C:\\a.cs:line 10\n   at A.C()");
        var b = ErrorFingerprint.Compute("app", "c", "y", "System.Exception: b\n   at A.B() in D:\\b.cs:line 99\n   at A.C()");
        var other = ErrorFingerprint.Compute("app", "c", "x", "System.TimeoutException: a\n   at A.B()");

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.Equal(ErrorFingerprint.Compute("api", "c", "Grupo 12 falhou após 300 ms", null), ErrorFingerprint.Compute("api", "c", "Grupo 7 falhou após 25 ms", null));
    }
}
