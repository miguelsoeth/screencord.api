using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Api.Realtime;
using ScreenCord.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Endpoints;

/// <summary>
/// Grupos: salas de presença. Quem cria entra como integrante e é o único que pode excluir o grupo;
/// qualquer integrante pode adicionar os próprios amigos. A entrada e a saída das salas acontecem pelo hub (<see cref="GroupsHubContract.Path"/>).
/// </summary>
public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/groups").RequireAuthorization().WithTags("Grupos");

        groups.MapGet("", GetGroupsAsync)
            .WithSummary("Lista os grupos de que você faz parte")
            .WithDescription("Cada grupo traz a quantidade de integrantes e quem está conectado à sala agora.")
            .Produces<List<GroupDto>>();
        groups.MapPost("", CreateGroupAsync)
            .WithSummary("Cria um grupo")
            .WithDescription($"O nome tem de 1 a {GroupsHubContract.MaxNameLength} caracteres. Quem cria vira o primeiro integrante. " +
                $"Cada conta pode ter até {GroupsHubContract.MaxCreatedGroups} grupos criados por ela (contam os que ela ainda integra).")
            .Produces<GroupDto>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        groups.MapGet("/{groupId:guid}", GetGroupAsync)
            .WithSummary("Integrantes de um grupo, com quem está conectado")
            .Produces<GroupDetailsDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        groups.MapPost("/{groupId:guid}/members", AddMemberAsync)
            .WithSummary("Adiciona um amigo seu ao grupo")
            .WithDescription($"Só integrantes podem adicionar, e só pessoas que são amigas de quem adiciona. Limite de {GroupsHubContract.MaxMembers} integrantes.")
            .Produces<GroupDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        groups.MapPut("/{groupId:guid}/settings", UpdateSettingsAsync)
            .WithSummary("Muda o que o grupo aceita transmitir")
            .WithDescription("Só quem criou o grupo pode. Define se a sala aceita compartilhamento de tela e câmera; as transmissões de um tipo desligado terminam na hora e todos os integrantes recebem GroupChanged.")
            .Produces<GroupSettings>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        groups.MapDelete("/{groupId:guid}/members/me", LeaveGroupAsync)
            .WithSummary("Sai do grupo")
            .WithDescription("O grupo é apagado quando o último integrante sai.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
        groups.MapDelete("/{groupId:guid}", DeleteGroupAsync)
            .WithSummary("Exclui o grupo para todos")
            .WithDescription("Só quem criou o grupo pode excluí-lo. Todos saem da sala, os compartilhamentos de tela terminam e cada integrante recebe GroupRemoved.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetGroupsAsync(HttpContext context, AppDbContext db, GroupPresence presence, CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var groups = await (
            from m in db.GroupMembers.AsNoTracking()
            where m.UserId == me
            join g in db.Groups.AsNoTracking() on m.GroupId equals g.Id
            orderby g.Name
            select new { g.Id, g.Name, g.AllowScreenShare, g.AllowCamera, Count = db.GroupMembers.Count(x => x.GroupId == g.Id) }).ToListAsync(cancellationToken);

        var snapshots = await GroupQueries.PresenceAsync(db, presence.Snapshot(groups.Select(g => g.Id)), cancellationToken);
        return Results.Ok(groups.Select(g =>
            new GroupDto(
                g.Id, g.Name, g.Count, snapshots[g.Id].ConnectedUsers, snapshots[g.Id].Version, snapshots[g.Id].ScreenShares,
                new GroupSettings(g.AllowScreenShare ?? true, g.AllowCamera ?? true))));
    }

    private static async Task<IResult> CreateGroupAsync(
        GroupCreate request,
        HttpContext context,
        AppDbContext db,
        GroupPresence presence,
        GroupNotifier notifier,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var name = string.Join(' ', (request.Name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is 0 or > GroupsHubContract.MaxNameLength)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_name", $"O nome do grupo deve ter de 1 a {GroupsHubContract.MaxNameLength} caracteres.");
        }

        // Grupos de que a pessoa saiu não contam: ela não consegue mais excluí-los para liberar espaço.
        var created = await db.Groups.CountAsync(
            g => g.CreatedById == me && db.GroupMembers.Any(m => m.GroupId == g.Id && m.UserId == me), cancellationToken);
        if (created >= GroupsHubContract.MaxCreatedGroups)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "group_limit",
                $"Você já criou {GroupsHubContract.MaxCreatedGroups} grupos, o limite por conta. Exclua um para criar outro.");
        }

        var now = time.GetUtcNow();
        var group = new Group { Id = Guid.NewGuid(), Name = name, CreatedById = me, CreatedAtUtc = now };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = me, AddedById = me, AddedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);

        // As outras janelas abertas da mesma conta também passam a ver o grupo.
        await notifier.GroupChangedAsync(db, group.Id, cancellationToken);

        var snapshot = await GroupQueries.PresenceAsync(db, presence, group.Id, cancellationToken);
        return Results.Created($"/groups/{group.Id}", new GroupDto(group.Id, group.Name, 1, snapshot.ConnectedUsers, snapshot.Version, snapshot.ScreenShares, group.Settings));
    }

    private static async Task<IResult> GetGroupAsync(Guid groupId, HttpContext context, AppDbContext db, GroupPresence presence, CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null || !await GroupQueries.IsMemberAsync(db, groupId, me, cancellationToken))
        {
            return GroupNotFound();
        }

        var users = await (
            from m in db.GroupMembers.AsNoTracking()
            where m.GroupId == groupId
            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
            orderby u.Username
            select u).ToListAsync(cancellationToken);

        var snapshot = presence.Snapshot(groupId);
        var connected = snapshot.UserIds.ToHashSet();
        var members = users
            .Select(u => new GroupMemberDto(u.Id, u.DiscordId, u.Username, u.GlobalName, u.Discriminator, u.AvatarHash, connected.Contains(u.Id), IsOwner: u.Id == group.CreatedById))
            .ToList();
        return Results.Ok(new GroupDetailsDto(
            group.Id, group.Name, members, snapshot.Version, CanDelete: group.CreatedById == me, Settings: group.Settings, CanEdit: group.CreatedById == me));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        Guid groupId,
        GroupSettingsUpdate request,
        HttpContext context,
        AppDbContext db,
        GroupPresence presence,
        GroupNotifier notifier,
        MediaRelay relay,
        CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null || !await GroupQueries.IsMemberAsync(db, groupId, me, cancellationToken))
        {
            return GroupNotFound();
        }

        if (group.CreatedById != me)
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, "not_group_owner", "Só quem criou o grupo pode mudar as configurações dele.");
        }

        group.AllowScreenShare = request.AllowScreenShare;
        group.AllowCamera = request.AllowCamera;
        await db.SaveChangesAsync(cancellationToken);

        // O que o grupo deixou de aceitar e já estava no ar termina agora.
        var settings = request.ToSettings();
        var stopped = presence.StopSharesNotAllowed(groupId, settings);
        foreach (var share in stopped)
        {
            relay.Stop(groupId, share.UserId, share.Kind);
        }

        await notifier.GroupChangedAsync(db, groupId, cancellationToken);
        if (stopped.Count > 0)
        {
            await notifier.PresenceChangedAsync(db, groupId, cancellationToken);
        }

        return Results.Ok(settings);
    }

    private static async Task<IResult> AddMemberAsync(
        Guid groupId,
        GroupMemberCreate request,
        HttpContext context,
        AppDbContext db,
        GroupPresence presence,
        GroupNotifier notifier,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        if (!await GroupQueries.IsMemberAsync(db, groupId, me, cancellationToken))
        {
            return GroupNotFound();
        }

        var (low, high) = Friendship.OrderPair(me, request.UserId);
        var isFriend = await db.Friendships.AnyAsync(
            f => f.UserLowId == low && f.UserHighId == high && f.Status == FriendshipStatus.Accepted,
            cancellationToken);
        if (request.UserId == me || !isFriend)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "not_friends", "Você só pode adicionar pessoas da sua lista de amigos.");
        }

        if (await GroupQueries.IsMemberAsync(db, groupId, request.UserId, cancellationToken))
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "already_member", "Esta pessoa já faz parte do grupo.");
        }

        if (await db.GroupMembers.CountAsync(m => m.GroupId == groupId, cancellationToken) >= GroupsHubContract.MaxMembers)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "group_full", $"O grupo atingiu o limite de {GroupsHubContract.MaxMembers} integrantes.");
        }

        db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = request.UserId, AddedById = me, AddedAtUtc = time.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "already_member", "Esta pessoa já faz parte do grupo.");
        }

        await notifier.GroupChangedAsync(db, groupId, cancellationToken);
        return Results.Ok(await GroupQueries.FindAsync(db, presence, groupId, cancellationToken));
    }

    private static async Task<IResult> LeaveGroupAsync(
        Guid groupId,
        HttpContext context,
        AppDbContext db,
        GroupPresence presence,
        GroupNotifier notifier,
        MediaRelay relay,
        CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var membership = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == me, cancellationToken);
        if (membership is null)
        {
            return GroupNotFound();
        }

        db.GroupMembers.Remove(membership);
        await db.SaveChangesAsync(cancellationToken);

        var remaining = await db.GroupMembers.CountAsync(m => m.GroupId == groupId, cancellationToken);
        if (remaining == 0)
        {
            await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync(cancellationToken);
        }

        // Quem saiu deixa de aparecer conectado (e de compartilhar ou assistir); os demais recebem a nova contagem.
        presence.RemoveUser(me, groupId);
        relay.StopAll(groupId, me);
        relay.DisconnectViewer(groupId, me);
        await notifier.GroupRemovedAsync(me, groupId, cancellationToken);
        if (remaining > 0)
        {
            await notifier.GroupChangedAsync(db, groupId, cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteGroupAsync(
        Guid groupId,
        HttpContext context,
        AppDbContext db,
        GroupPresence presence,
        GroupNotifier notifier,
        MediaRelay relay,
        CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null || !await GroupQueries.IsMemberAsync(db, groupId, me, cancellationToken))
        {
            return GroupNotFound();
        }

        if (group.CreatedById != me)
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, "not_group_owner", "Só quem criou o grupo pode excluí-lo.");
        }

        var members = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        await db.GroupMembers.Where(m => m.GroupId == groupId).ExecuteDeleteAsync(cancellationToken);
        await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync(cancellationToken);

        // Todos saem da sala (e param de compartilhar ou assistir); cada janela aberta tira o grupo da lista.
        foreach (var member in members)
        {
            presence.RemoveUser(member, groupId);
            relay.StopAll(groupId, member);
            relay.DisconnectViewer(groupId, member);
            await notifier.GroupRemovedAsync(member, groupId, cancellationToken);
        }

        return Results.NoContent();
    }

    private static IResult GroupNotFound() =>
        ApiResults.Error(StatusCodes.Status404NotFound, "group_not_found", "Grupo não encontrado ou você não faz parte dele.");
}
