using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Endpoints;

public static class FriendEndpoints
{
    public const int MinSearchLength = 2;
    public const int MaxSearchResults = 20;

    public static void MapFriendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization();

        group.MapGet("/users/search", SearchUsersAsync);
        group.MapGet("/friends", GetFriendsAsync);
        group.MapDelete("/friends/{userId:guid}", RemoveFriendAsync);
        group.MapGet("/friends/requests", GetRequestsAsync);
        group.MapPost("/friends/requests", SendRequestAsync);
        group.MapPost("/friends/requests/{userId:guid}/accept", AcceptRequestAsync);
        group.MapDelete("/friends/requests/{userId:guid}", DeleteRequestAsync);
    }

    /// <summary>Busca usuários do sistema por nome de usuário ou nome de exibição (o "@" inicial é ignorado).</summary>
    private static async Task<IResult> SearchUsersAsync(string? q, HttpContext context, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var term = (q ?? "").Trim().TrimStart('@').Trim();
        if (term.Length < MinSearchLength)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "query_too_short", $"Digite pelo menos {MinSearchLength} caracteres para buscar.");
        }

        if (term.Length > 64)
        {
            term = term[..64];
        }

        var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Id != me &&
                        (EF.Functions.Like(u.Username, pattern, "\\") ||
                         (u.GlobalName != null && EF.Functions.Like(u.GlobalName, pattern, "\\"))))
            .OrderBy(u => u.Username)
            .Take(MaxSearchResults)
            .ToListAsync(cancellationToken);

        var ids = users.Select(u => u.Id).ToList();
        var relations = await db.Friendships.AsNoTracking()
            .Where(f => (f.RequesterId == me && ids.Contains(f.AddresseeId)) ||
                        (f.AddresseeId == me && ids.Contains(f.RequesterId)))
            .ToListAsync(cancellationToken);

        var result = users.Select(u =>
        {
            var relation = relations.FirstOrDefault(f => f.RequesterId == u.Id || f.AddresseeId == u.Id);
            return u.ToDto(StatusFor(relation, me));
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> GetFriendsAsync(HttpContext context, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var friends = await (
            from f in db.Friendships.AsNoTracking()
            where f.Status == FriendshipStatus.Accepted && (f.RequesterId == me || f.AddresseeId == me)
            join u in db.Users.AsNoTracking() on (f.RequesterId == me ? f.AddresseeId : f.RequesterId) equals u.Id
            orderby u.Username
            select u).ToListAsync(cancellationToken);

        return Results.Ok(friends.Select(u => u.ToDto(RelationshipStatus.Friend)));
    }

    private static async Task<IResult> GetRequestsAsync(HttpContext context, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();

        var incoming = await (
            from f in db.Friendships.AsNoTracking()
            where f.Status == FriendshipStatus.Pending && f.AddresseeId == me
            join u in db.Users.AsNoTracking() on f.RequesterId equals u.Id
            orderby u.Username
            select u).ToListAsync(cancellationToken);

        var outgoing = await (
            from f in db.Friendships.AsNoTracking()
            where f.Status == FriendshipStatus.Pending && f.RequesterId == me
            join u in db.Users.AsNoTracking() on f.AddresseeId equals u.Id
            orderby u.Username
            select u).ToListAsync(cancellationToken);

        return Results.Ok(new FriendRequestsResponse(
            incoming.Select(u => u.ToDto(RelationshipStatus.PendingIncoming)).ToList(),
            outgoing.Select(u => u.ToDto(RelationshipStatus.PendingOutgoing)).ToList()));
    }

    private static async Task<IResult> SendRequestAsync(
        FriendRequestCreate request,
        HttpContext context,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        if (request.UserId == me)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "cannot_add_self", "Você não pode adicionar a si mesmo.");
        }

        var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);
        if (target is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "user_not_found", "Usuário não encontrado.");
        }

        var existing = await FindAsync(db, me, target.Id, cancellationToken);
        if (existing is null)
        {
            var (low, high) = Friendship.OrderPair(me, target.Id);
            db.Friendships.Add(new Friendship
            {
                Id = Guid.NewGuid(),
                RequesterId = me,
                AddresseeId = target.Id,
                UserLowId = low,
                UserHighId = high,
                Status = FriendshipStatus.Pending,
                CreatedAtUtc = time.GetUtcNow(),
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return ApiResults.Error(StatusCodes.Status409Conflict, "conflict", "A relação com este usuário acabou de mudar. Atualize e tente novamente.");
            }

            return Results.Created($"/friends/requests/{target.Id}", target.ToDto(RelationshipStatus.PendingOutgoing));
        }

        if (existing.Status == FriendshipStatus.Accepted)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "already_friends", "Vocês já são amigos.");
        }

        if (existing.RequesterId == me)
        {
            return Results.Ok(target.ToDto(RelationshipStatus.PendingOutgoing));
        }

        // A outra pessoa já tinha pedido: os dois querem, então a amizade é confirmada.
        existing.Status = FriendshipStatus.Accepted;
        existing.AcceptedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(target.ToDto(RelationshipStatus.Friend));
    }

    private static async Task<IResult> AcceptRequestAsync(
        Guid userId,
        HttpContext context,
        AppDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var request = await db.Friendships.FirstOrDefaultAsync(
            f => f.Status == FriendshipStatus.Pending && f.RequesterId == userId && f.AddresseeId == me,
            cancellationToken);
        if (request is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "request_not_found", "Não há solicitação pendente deste usuário.");
        }

        request.Status = FriendshipStatus.Accepted;
        request.AcceptedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Recusa uma solicitação recebida ou cancela uma enviada.</summary>
    private static async Task<IResult> DeleteRequestAsync(Guid userId, HttpContext context, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var existing = await FindAsync(db, me, userId, cancellationToken);
        if (existing is not { Status: FriendshipStatus.Pending })
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "request_not_found", "Não há solicitação pendente com este usuário.");
        }

        db.Friendships.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFriendAsync(Guid userId, HttpContext context, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = context.User.GetUserId();
        var existing = await FindAsync(db, me, userId, cancellationToken);
        if (existing is not { Status: FriendshipStatus.Accepted })
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "not_friends", "Este usuário não está na sua lista de amigos.");
        }

        db.Friendships.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static Task<Friendship?> FindAsync(AppDbContext db, Guid a, Guid b, CancellationToken cancellationToken)
    {
        var (low, high) = Friendship.OrderPair(a, b);
        return db.Friendships.FirstOrDefaultAsync(f => f.UserLowId == low && f.UserHighId == high, cancellationToken);
    }

    private static RelationshipStatus StatusFor(Friendship? friendship, Guid me) => friendship switch
    {
        null => RelationshipStatus.None,
        { Status: FriendshipStatus.Accepted } => RelationshipStatus.Friend,
        _ when friendship.RequesterId == me => RelationshipStatus.PendingOutgoing,
        _ => RelationshipStatus.PendingIncoming,
    };
}
