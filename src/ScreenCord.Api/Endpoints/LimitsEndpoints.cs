using System.Text.Json;
using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Endpoints;

/// <summary>
/// Qualidade padrão do compartilhamento pela API (seção "RelayLimits" do appsettings ou do api.env), usada
/// enquanto ninguém ajustar pelo painel (<see cref="RelayLimitsStore"/>). A banda de saída do servidor é a taxa
/// vezes o número de espectadores, então o padrão é baixo (<see cref="StreamLimits.Relay"/>).
/// </summary>
/// <remarks>
/// <c>Profiles</c> lista as combinações aceitas (<c>RelayLimits__Profiles__0__Height=720</c>,
/// <c>RelayLimits__Profiles__0__FrameRate=60</c>, ...). <c>MaxHeight</c> e <c>MaxFrameRate</c> são o formato antigo
/// (um par só) e valem quando não há <c>Profiles</c>.
/// </remarks>
public sealed class RelayLimitsOptions
{
    public const string SectionName = "RelayLimits";

    public int? MaxHeight { get; set; }

    public int? MaxFrameRate { get; set; }

    public int? MaxBitrateKbps { get; set; }

    public List<StreamProfile>? Profiles { get; set; }

    public StreamLimits ToLimits()
    {
        var kbps = MaxBitrateKbps ?? StreamLimits.Relay.MaxBitrateKbps;
        if (Profiles is { Count: > 0 })
        {
            return StreamLimits.FromProfiles(Profiles, kbps);
        }

        return MaxHeight is null && MaxFrameRate is null
            ? StreamLimits.Relay with { MaxBitrateKbps = kbps }
            : new StreamLimits(MaxHeight ?? StreamLimits.Relay.MaxHeight, MaxFrameRate ?? StreamLimits.Relay.MaxFrameRate, kbps);
    }
}

/// <summary>
/// Os limites em vigor do compartilhamento pela API. Começam nos padrões (<see cref="RelayLimitsOptions"/>); o que o
/// dono salva no painel <c>/admin</c> fica no banco, vale já para os próximos compartilhamentos e sobrevive a
/// deploys. A API roda numa instância só, então o valor em memória é o de todos.
/// </summary>
public sealed class RelayLimitsStore(IOptions<RelayLimitsOptions> options, TimeProvider time)
{
    public const string SettingKey = "RelayLimits";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private volatile StreamLimits _current = options.Value.ToLimits();

    public StreamLimits Current => _current;

    /// <summary>Os limites sem ajuste do painel.</summary>
    public StreamLimits Defaults => options.Value.ToLimits();

    /// <summary>Quando o painel ajustou os limites; nulo enquanto valem os padrões.</summary>
    public DateTimeOffset? CustomSince { get; private set; }

    /// <summary>Carrega o ajuste do painel, se houver; na inicialização, antes de atender.</summary>
    public void Load(AppDbContext db)
    {
        var row = db.Settings.AsNoTracking().FirstOrDefault(s => s.Key == SettingKey);
        if (row is not null && Parse(row.Value) is { } limits)
        {
            _current = limits;
            CustomSince = row.UpdatedAtUtc;
        }
    }

    public async Task SaveAsync(AppDbContext db, StreamLimits limits, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var value = JsonSerializer.Serialize(limits, Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSetting { Key = SettingKey, Value = value, UpdatedAtUtc = now });
        }
        else
        {
            row.Value = value;
            row.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        _current = limits;
        CustomSince = now;
    }

    /// <summary>Descarta o ajuste do painel e volta aos padrões.</summary>
    public async Task ResetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Settings.Where(s => s.Key == SettingKey).ExecuteDeleteAsync(cancellationToken);
        _current = Defaults;
        CustomSince = null;
    }

    /// <summary>
    /// Monta os limites a partir do que o painel mandou; nulo se algo estiver fora do que o aplicativo sabe fazer.
    /// Fica só com as combinações que não são cobertas por outra maior (720p a 60 fps cobre 720p a 30 fps).
    /// </summary>
    public static StreamLimits? Build(IReadOnlyList<StreamProfile>? profiles, int maxBitrateKbps)
    {
        if (profiles is not { Count: > 0 and <= 6 })
        {
            return null;
        }

        var kept = profiles.Distinct()
            .Where(p => !profiles.Any(o => o != p && p.Height <= o.Height && p.FrameRate <= o.FrameRate))
            .OrderBy(p => p.Height).ThenBy(p => p.FrameRate)
            .ToList();
        var limits = StreamLimits.FromProfiles(kept, maxBitrateKbps);
        return limits.IsSane ? limits : null;
    }

    private static StreamLimits? Parse(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<StreamLimits>(value, Json) is { IsSane: true } limits ? limits : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Corpo de <c>PUT /admin/api/limits</c>.</summary>
public sealed record RelayLimitsUpdate(IReadOnlyList<StreamProfile>? Profiles, int MaxBitrateKbps);

/// <summary>Resposta de <c>/admin/api/limits</c>: os limites em vigor, os padrões e o que o painel pode escolher.</summary>
public sealed record RelayLimitsView(
    StreamLimits Relay,
    StreamLimits Defaults,
    bool Custom,
    DateTimeOffset? UpdatedAtUtc,
    int[] Heights,
    int[] FrameRates,
    int MinBitrateKbps,
    int MaxBitrateKbps);

public static class LimitsEndpoints
{
    public static void MapLimitsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/limits", (RelayLimitsStore limits) => Results.Ok(new ServerLimitsDto(limits.Current)))
            .WithTags("Infra")
            .WithSummary("Limites do compartilhamento de tela pela API")
            .WithDescription("O aplicativo só oferece as resoluções, taxas de quadros e taxa de bits que cabem aqui; a API recusa o resto em StartScreenShare. Os limites mudam pelo painel, sem novo deploy.")
            .Produces<ServerLimitsDto>();
    }

    /// <summary>Edição dos limites no painel; <paramref name="admin"/> é o grupo <c>/admin/api</c>, que exige o token.</summary>
    public static void MapLimitsAdminEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/limits", (RelayLimitsStore limits) => Results.Ok(View(limits)));

        admin.MapPut("/limits", async (RelayLimitsUpdate? update, RelayLimitsStore limits, AppDbContext db, CancellationToken cancellationToken) =>
        {
            if (update is null || RelayLimitsStore.Build(update.Profiles, update.MaxBitrateKbps) is not { } built)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_limits", "Limites fora do que o aplicativo sabe fazer.");
            }

            await limits.SaveAsync(db, built, cancellationToken);
            return Results.Ok(View(limits));
        });

        admin.MapDelete("/limits", async (RelayLimitsStore limits, AppDbContext db, CancellationToken cancellationToken) =>
        {
            await limits.ResetAsync(db, cancellationToken);
            return Results.Ok(View(limits));
        });
    }

    private static RelayLimitsView View(RelayLimitsStore limits) => new(
        limits.Current,
        limits.Defaults,
        limits.CustomSince is not null,
        limits.CustomSince,
        ScreenShareLimits.Heights,
        ScreenShareLimits.FrameRates,
        ScreenShareLimits.MinBitrateKbps,
        ScreenShareLimits.MaxConfigurableBitrateKbps);
}
