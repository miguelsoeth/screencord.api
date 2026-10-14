using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ScreenCord.Api.Realtime;

/// <summary>
/// Conexão persistente do aplicativo (SignalR, normalmente sobre WebSocket), autenticada com o mesmo
/// token de sessão das rotas REST no cabeçalho <c>Authorization</c>. O hub controla só a presença nas
/// salas; criar grupos e adicionar integrantes fica nas rotas REST, que também notificam por aqui.
/// </summary>
[Authorize]
public sealed class GroupsHub(GroupPresence presence, GroupNotifier notifier, AppDbContext db, ILogger<GroupsHub> logger) : Hub
{
    /// <summary>Entra na sala do grupo, saindo da anterior. Retorna null se o usuário não for integrante.</summary>
    public async Task<GroupPresenceDto?> JoinGroup(Guid groupId)
    {
        var me = Context.User!.GetUserId();
        if (!await GroupQueries.IsMemberAsync(db, groupId, me, Context.ConnectionAborted))
        {
            return null;
        }

        var previous = presence.Join(Context.ConnectionId, me, groupId);
        if (previous is { } left)
        {
            await notifier.PresenceChangedAsync(db, left);
        }

        await notifier.PresenceChangedAsync(db, groupId);
        return await GroupQueries.PresenceAsync(db, presence, groupId, Context.ConnectionAborted);
    }

    public async Task LeaveGroup()
    {
        if (presence.Leave(Context.ConnectionId) is { } left)
        {
            await notifier.PresenceChangedAsync(db, left);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (presence.Leave(Context.ConnectionId) is { } left)
        {
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
}
