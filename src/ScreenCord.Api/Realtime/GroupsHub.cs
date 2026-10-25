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

        // Trocar de sala encerra as transmissões (tela e câmera) feitas pela sala anterior.
        foreach (var stopped in presence.StopSharesIfInOtherGroup(Context.ConnectionId, groupId))
        {
            relay.Stop(stopped.GroupId, stopped.UserId, stopped.Kind);
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
        StopSharesOfThisConnection();
        if (presence.Leave(Context.ConnectionId) is { } left)
        {
            DisconnectViewerIfGone(Context.User!.GetUserId(), left);
            await notifier.PresenceChangedAsync(db, left);
        }
    }

    /// <summary>
    /// Começa a transmitir a tela ou a câmera (<see cref="ScreenShareStart.Kind"/>) na sala em que esta conexão está; a outra
    /// transmissão da mesma pessoa continua. Retorna null se a conexão não estiver em nenhuma sala, se as configurações
    /// passarem dos limites do servidor ou se o grupo não aceitar o tipo. Depois disso, o aplicativo abre o canal de mídia.
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

    /// <summary>Encerra todas as transmissões desta conexão (o formato das versões que só tinham uma).</summary>
    public async Task StopScreenShare()
    {
        if (StopSharesOfThisConnection() is { } groupId)
        {
            await notifier.PresenceChangedAsync(db, groupId);
        }
    }

    /// <summary>Encerra só a transmissão desse tipo desta conexão; a outra (tela ou câmera) continua.</summary>
    public async Task StopShare(ShareKind kind)
    {
        if (presence.StopShare(Context.ConnectionId, kind) is not { } stopped)
        {
            return;
        }

        relay.Stop(stopped.GroupId, stopped.UserId, stopped.Kind);
        await notifier.PresenceChangedAsync(db, stopped.GroupId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        StopSharesOfThisConnection();
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

    /// <summary>Encerra as transmissões desta conexão, se houver. Retorna a sala em que elas aconteciam.</summary>
    private Guid? StopSharesOfThisConnection()
    {
        var stopped = presence.StopShares(Context.ConnectionId);
        foreach (var share in stopped)
        {
            relay.Stop(share.GroupId, share.UserId, share.Kind);
        }

        return stopped.Count > 0 ? stopped[0].GroupId : null;
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
