using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Api.Endpoints;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ScreenCord.Api.Realtime;

/// <summary>
/// Conexão persistente do aplicativo (SignalR, normalmente sobre WebSocket), autenticada com o mesmo
/// token de sessão das rotas REST no cabeçalho <c>Authorization</c>. O hub controla a presença nas
/// salas e anuncia os compartilhamentos de tela; criar grupos e adicionar integrantes fica nas rotas
/// REST, que também notificam por aqui. A mídia do compartilhamento passa por <see cref="MediaRelay"/>.
/// </summary>
[Authorize]
public sealed class GroupsHub(
    GroupPresence presence,
    GroupNotifier notifier,
    MediaRelay relay,
    AppDbContext db,
    RelayLimitsStore relayLimits,
    ILogger<GroupsHub> logger) : Hub
{
    /// <summary>Entra na sala do grupo, saindo da anterior. Retorna null se o usuário não for integrante.</summary>
    public async Task<GroupPresenceDto?> JoinGroup(Guid groupId)
    {
        var me = Context.User!.GetUserId();
        if (!await GroupQueries.IsMemberAsync(db, groupId, me, Context.ConnectionAborted))
        {
            return null;
        }

        // Trocar de sala encerra o compartilhamento de tela feito pela sala anterior.
        var share = presence.StopShareIfInOtherGroup(Context.ConnectionId, groupId);
        if (share is { } stopped)
        {
            relay.Stop(stopped.GroupId, stopped.UserId);
        }

        var previous = presence.Join(Context.ConnectionId, me, groupId);
        if (previous is { } left)
        {
            DisconnectViewerIfGone(me, left);
            await notifier.PresenceChangedAsync(db, left);
        }

        await notifier.PresenceChangedAsync(db, groupId);
        return await GroupQueries.PresenceAsync(db, presence, groupId, Context.ConnectionAborted);
    }

    public async Task LeaveGroup()
    {
        StopShareOfThisConnection();
        if (presence.Leave(Context.ConnectionId) is { } left)
        {
            DisconnectViewerIfGone(Context.User!.GetUserId(), left);
            await notifier.PresenceChangedAsync(db, left);
        }
    }

    /// <summary>
    /// Começa a compartilhar a tela na sala em que esta conexão está. Retorna null se a conexão não estiver
    /// em nenhuma sala ou se as configurações passarem dos limites do servidor. Depois disso, o aplicativo abre o canal de mídia.
    /// </summary>
    public async Task<ScreenShareDto?> StartScreenShare(ScreenShareStart start)
    {
        if (!ScreenShareLimits.IsValid(start, relayLimits.Current) || !Enum.IsDefined(start.Kind) || presence.GroupOf(Context.ConnectionId) is not { } roomId)
        {
            return null;
        }

        // O dono do grupo decide se a sala aceita tela e câmera.
        if (!(await GroupQueries.SettingsAsync(db, roomId, Context.ConnectionAborted)).Allows(start.Kind) ||
            presence.StartShare(Context.ConnectionId, start) is not { } started)
        {
            return null;
        }

        await notifier.PresenceChangedAsync(db, started.GroupId);
        return started.Share;
    }

    public async Task StopScreenShare()
    {
        if (StopShareOfThisConnection() is { } groupId)
        {
            await notifier.PresenceChangedAsync(db, groupId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        StopShareOfThisConnection();
        if (presence.Leave(Context.ConnectionId) is { } left)
        {
            DisconnectViewerIfGone(Context.User!.GetUserId(), left);
            try
            {
                await notifier.PresenceChangedAsync(db, left);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao avisar a saída de uma conexão do grupo {GroupId}.", left);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Encerra o compartilhamento desta conexão, se houver. Retorna a sala em que ele acontecia.</summary>
    private Guid? StopShareOfThisConnection()
    {
        if (presence.StopShare(Context.ConnectionId) is not { } stopped)
        {
            return null;
        }

        relay.Stop(stopped.GroupId, stopped.UserId);
        return stopped.GroupId;
    }

    /// <summary>Quem saiu da sala (por todas as conexões) para de receber os compartilhamentos dela.</summary>
    private void DisconnectViewerIfGone(Guid userId, Guid groupId)
    {
        if (!presence.IsConnected(userId, groupId))
        {
            relay.DisconnectViewer(groupId, userId);
        }
    }
}
