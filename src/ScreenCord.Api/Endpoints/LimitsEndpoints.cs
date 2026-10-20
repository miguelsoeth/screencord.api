using ScreenCord.Contracts;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Endpoints;

/// <summary>
/// Qualidade máxima do compartilhamento pela API (seção "RelayLimits" do appsettings). A banda de saída do
/// servidor é a taxa vezes o número de espectadores, então o padrão é baixo (<see cref="StreamLimits.Relay"/>).
/// </summary>
public sealed class RelayLimitsOptions
{
    public const string SectionName = "RelayLimits";

    public int MaxHeight { get; set; } = StreamLimits.Relay.MaxHeight;

    public int MaxFrameRate { get; set; } = StreamLimits.Relay.MaxFrameRate;

    public int MaxBitrateKbps { get; set; } = StreamLimits.Relay.MaxBitrateKbps;

    public StreamLimits ToLimits() => new(MaxHeight, MaxFrameRate, MaxBitrateKbps);
}

public static class LimitsEndpoints
{
    public static void MapLimitsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/limits", (IOptions<RelayLimitsOptions> relay) => Results.Ok(new ServerLimitsDto(relay.Value.ToLimits())))
            .WithTags("Infra")
            .WithSummary("Limites do compartilhamento de tela pela API")
            .WithDescription("O aplicativo só oferece as resoluções, taxas de quadros e taxa de bits que cabem aqui; a API recusa o resto em StartScreenShare.")
            .Produces<ServerLimitsDto>();
    }
}
