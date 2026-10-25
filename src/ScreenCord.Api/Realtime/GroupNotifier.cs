using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Realtime;

/// <summary>Envia as mudanças de um grupo a todos os integrantes com o aplicativo aberto (todas as conexões de cada um).</summary>
public sealed class GroupNotifier(IHubContext<GroupsHub> hub, GroupPresence presence)
{
    public async Task PresenceChangedAsync(AppDbContext db, Guid groupId, CancellationToken cancellationToken = default)
    {
        var members = await MemberIdsAsync(db, groupId, cancellationToken);
        if (members.Count > 0)
        {
            var snapshot = await GroupQueries.PresenceAsync(db, presence, groupId, cancellationToken);
            await hub.Clients.Users(members).SendAsync(GroupsHubContract.PresenceChanged, snapshot, cancellationToken);
        }
    }

    /// <summary>Grupo criado ou com integrantes alterados: todos os integrantes recebem a versão atual.</summary>
    public async Task GroupChangedAsync(AppDbContext db, Guid groupId, CancellationToken cancellationToken = default)
    {
        var group = await GroupQueries.FindAsync(db, presence, groupId, cancellationToken);
        var members = await MemberIdsAsync(db, groupId, cancellationToken);
        if (group is not null && members.Count > 0)
        {
            await hub.Clients.Users(members).SendAsync(GroupsHubContract.GroupChanged, group, cancellationToken);
        }
    }

    /// <summary>Avisa as outras janelas do usuário de que ele não faz mais parte do grupo.</summary>
    public Task GroupRemovedAsync(Guid userId, Guid groupId, CancellationToken cancellationToken = default) =>
        hub.Clients.User(userId.ToString()).SendAsync(GroupsHubContract.GroupRemoved, groupId, cancellationToken);

    private static async Task<List<string>> MemberIdsAsync(AppDbContext db, Guid groupId, CancellationToken cancellationToken)
    {
        var ids = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        return ids.ConvertAll(id => id.ToString());
    }
}

public static class GroupQueries
{
    public static async Task<GroupDto?> FindAsync(AppDbContext db, GroupPresence presence, Guid groupId, CancellationToken cancellationToken)
    {
        var group = await db.Groups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => new { g.Id, g.Name, g.AllowScreenShare, g.AllowCamera, Count = db.GroupMembers.Count(m => m.GroupId == g.Id) })
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null)
        {
            return null;
        }

        var snapshot = await PresenceAsync(db, presence, group.Id, cancellationToken);
        return new GroupDto(
            group.Id, group.Name, group.Count, snapshot.ConnectedUsers, snapshot.Version, snapshot.ScreenShares,
            new GroupSettings(group.AllowScreenShare ?? true, group.AllowCamera ?? true));
    }

    /// <summary>O que o grupo aceita transmitir; grupo que não existe não aceita nada.</summary>
    public static async Task<GroupSettings> SettingsAsync(AppDbContext db, Guid groupId, CancellationToken cancellationToken)
    {
        var flags = await db.Groups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => new { g.AllowScreenShare, g.AllowCamera })
            .FirstOrDefaultAsync(cancellationToken);
        return flags is null
            ? new GroupSettings(false, false)
            : new GroupSettings(flags.AllowScreenShare ?? true, flags.AllowCamera ?? true);
    }

    public static async Task<GroupPresenceDto> PresenceAsync(AppDbContext db, GroupPresence presence, Guid groupId, CancellationToken cancellationToken) =>
        (await PresenceAsync(db, presence.Snapshot([groupId]), cancellationToken))[groupId];

    /// <summary>Completa as fotos da presença com nome e avatar dos usuários, numa consulta só.</summary>
    public static async Task<IReadOnlyDictionary<Guid, GroupPresenceDto>> PresenceAsync(
        AppDbContext db,
        IReadOnlyList<PresenceSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var ids = snapshots.SelectMany(s => s.UserIds).Distinct().ToList();
        var users = ids.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, cancellationToken);

        return snapshots.ToDictionary(
            s => s.GroupId,
            s => new GroupPresenceDto(
                s.GroupId,
                s.UserIds
                    .Where(users.ContainsKey)
                    .Select(id => users[id])
                    .OrderBy(u => u.GlobalName ?? u.Username, StringComparer.CurrentCultureIgnoreCase)
                    .Select(u => new PresenceUserDto(u.Id, u.DiscordId, u.Username, u.GlobalName, u.Discriminator, u.AvatarHash))
                    .ToList(),
                s.Version,
                s.Shares.Where(share => users.ContainsKey(share.UserId)).ToList()));
    }

    public static Task<bool> IsMemberAsync(AppDbContext db, Guid groupId, Guid userId, CancellationToken cancellationToken) =>
        db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, cancellationToken);
}
