using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Api.Realtime;
using ScreenCord.Contracts;

namespace ScreenCord.Api.Endpoints;

/// <summary>
/// Canal de mídia de uma transmissão (WebSocket): a tela ou a câmera de uma pessoa, escolhida pelo parâmetro
/// <see cref="MediaContract.KindQuery"/> (sem ele, a tela). A transmissão é anunciada pelo hub
/// (<see cref="GroupsHubContract.StartScreenShare"/>); aqui só trafegam os pacotes de vídeo e áudio.
/// </summary>
public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(MediaContract.Path + "/{groupId:guid}/{sharerId:guid}", HandleAsync)
            .RequireAuthorization()
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(
        Guid groupId,
        Guid sharerId,
        HttpContext context,
        AppDbContext db,
        GroupPresence presence,
        GroupNotifier notifier,
        MediaRelay relay,
        ILogger<MediaRelay> logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "websocket_required", "Este endereço aceita somente WebSocket.");
        }

        var me = context.User.GetUserId();
        var aborted = context.RequestAborted;
        if (!await GroupQueries.IsMemberAsync(db, groupId, me, aborted))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "group_not_found", "Grupo não encontrado ou você não faz parte dele.");
        }

        // Sem o parâmetro (versões do app que só tinham uma transmissão por pessoa) vale a que a pessoa tiver: a tela, senão a câmera.
        var kind = context.Request.Query.ContainsKey(MediaContract.KindQuery)
            ? MediaContract.ParseKind(context.Request.Query[MediaContract.KindQuery])
            : presence.FindShare(groupId, sharerId, ShareKind.Screen)?.Kind ?? ShareKind.Camera;
        var share = presence.FindShare(groupId, sharerId, kind);
        if (sharerId == me)
        {
            if (share is null)
            {
                return ApiResults.Error(StatusCodes.Status409Conflict, "share_not_started", "Comece o compartilhamento pela conexão em tempo real antes de enviar a mídia.");
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var wasCurrent = await relay.PublishAsync(socket, groupId, me, kind, share.BitrateKbps, aborted);

            // O canal caiu sem que a transmissão fosse encerrada pelo hub: quem assiste precisa saber.
            if (wasCurrent && presence.StopShare(groupId, me, kind))
            {
                logger.LogInformation("Compartilhamento de tela encerrado pela queda do canal de mídia.");
                await notifier.PresenceChangedAsync(db, groupId, CancellationToken.None);
            }

            return Results.Empty;
        }

        if (!presence.IsConnected(me, groupId))
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, "not_in_room", "Entre na sala do grupo para assistir.");
        }

        if (share is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "share_not_found", "Este compartilhamento de tela já terminou.");
        }

        using (var socket = await context.WebSockets.AcceptWebSocketAsync())
        {
            await relay.WatchAsync(socket, groupId, sharerId, kind, me, () => presence.FindShare(groupId, sharerId, kind) is not null, aborted);
        }

        return Results.Empty;
    }
}
