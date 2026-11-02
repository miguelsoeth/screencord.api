using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Api.Realtime;
using ScreenCord.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Endpoints;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("").WithTags("Autenticação");

        auth.MapPost("/auth/discord", LoginWithDiscordAsync)
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithSummary("Entra com um access token do Discord")
            .WithDescription("Confirma o token com o Discord (GET /oauth2/@me), cria ou atualiza o usuário e devolve um token de sessão desta API.")
            .Produces<LoginResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        auth.MapPost("/auth/logout", LogoutAsync)
            .RequireAuthorization()
            .WithSummary("Encerra a sessão atual")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapGet("/me", GetMeAsync)
            .RequireAuthorization()
            .WithSummary("Usuário da sessão")
            .Produces<UserDto>();

        auth.MapPost("/me/discord", SyncWithDiscordAsync)
            .RequireAuthorization()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithSummary("Atualiza nome e foto com o Discord")
            .WithDescription("Confirma o token com o Discord, grava o nome e a foto atuais e, se mudaram, avisa amigos e grupos da pessoa.")
            .Produces<UserDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Troca um access token do Discord por uma sessão desta API. No primeiro login o usuário é criado;
    /// nos seguintes, nome e avatar são atualizados com o que o Discord informar.
    /// </summary>
    private static async Task<IResult> LoginWithDiscordAsync(
        DiscordLoginRequest request,
        IDiscordTokenVerifier verifier,
        AppDbContext db,
        TimeProvider time,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AccessToken) || request.AccessToken.Length > 512)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_request", "Token do Discord ausente ou inválido.");
        }

        var logger = loggerFactory.CreateLogger("Auth");

        DiscordIdentity identity;
        try
        {
            identity = await verifier.VerifyAsync(request.AccessToken, cancellationToken);
        }
        catch (DiscordVerificationException ex)
        {
            // O token nunca é registrado; só o motivo da recusa.
            logger.LogWarning("Login recusado: {Failure}.", ex.Failure);
            return VerificationError(ex.Failure);
        }

        var now = time.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == identity.Id, cancellationToken);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                DiscordId = identity.Id,
                Username = identity.Username,
                CreatedAtUtc = now,
            };
            db.Users.Add(user);
            logger.LogInformation("Usuário criado para a conta do Discord {DiscordId}.", identity.Id);
        }

        ApplyIdentity(user, identity);
        user.LastLoginAtUtc = now;

        var lifetimeDays = Math.Clamp(configuration.GetValue("Session:LifetimeDays", 30), 1, 365);
        var token = SessionTokens.Create();
        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = SessionTokens.Hash(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(lifetimeDays),
        };
        db.Sessions.Add(session);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Dois primeiros logins simultâneos da mesma conta: o índice único barra o segundo.
            return ApiResults.Error(StatusCodes.Status409Conflict, "conflict", "Login em andamento. Tente novamente.");
        }

        // Limpeza oportunista das sessões vencidas deste usuário.
        var expired = await db.Sessions.Where(s => s.UserId == user.Id).ToListAsync(cancellationToken);
        db.Sessions.RemoveRange(expired.Where(s => s.ExpiresAtUtc <= now));
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new LoginResponse(token, session.ExpiresAtUtc, user.ToDto(RelationshipStatus.None)));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, AppDbContext db, CancellationToken cancellationToken)
    {
        var sessionId = context.User.GetSessionId();
        await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMeAsync(HttpContext context, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = context.User.GetUserId();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null
            ? ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sessão inválida.")
            : Results.Ok(user.ToDto(RelationshipStatus.None));
    }

    /// <summary>
    /// Traz o nome e a foto do Discord sem abrir outra sessão. Só quem está conectado à conta pode: o token precisa ser
    /// da mesma conta do Discord que a sessão. Se algo mudou, a presença nos grupos e as listas de amigos se atualizam.
    /// </summary>
    private static async Task<IResult> SyncWithDiscordAsync(
        DiscordLoginRequest request,
        HttpContext context,
        IDiscordTokenVerifier verifier,
        AppDbContext db,
        GroupNotifier groups,
        FriendNotifier friends,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AccessToken) || request.AccessToken.Length > 512)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_request", "Token do Discord ausente ou inválido.");
        }

        DiscordIdentity identity;
        try
        {
            identity = await verifier.VerifyAsync(request.AccessToken, cancellationToken);
        }
        catch (DiscordVerificationException ex)
        {
            loggerFactory.CreateLogger("Auth").LogWarning("Atualização do perfil recusada: {Failure}.", ex.Failure);
            return VerificationError(ex.Failure);
        }

        var me = context.User.GetUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == me, cancellationToken);
        if (user is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized", "Sessão inválida.");
        }

        if (user.DiscordId != identity.Id)
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, "discord_account_mismatch", "Este token é de outra conta do Discord.");
        }

        var changed = user.Username != identity.Username || user.GlobalName != identity.GlobalName ||
            user.Discriminator != identity.Discriminator || user.AvatarHash != identity.AvatarHash;
        if (changed)
        {
            ApplyIdentity(user, identity);
            await db.SaveChangesAsync(cancellationToken);

            var memberOf = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == me).Select(m => m.GroupId).ToListAsync(cancellationToken);
            foreach (var groupId in memberOf)
            {
                await groups.PresenceChangedAsync(db, groupId, cancellationToken);
            }

            var related = await db.Friendships.AsNoTracking()
                .Where(f => f.UserLowId == me || f.UserHighId == me)
                .Select(f => f.UserLowId == me ? f.UserHighId : f.UserLowId)
                .ToListAsync(cancellationToken);
            await friends.ProfileChangedAsync(related, cancellationToken);
        }

        return Results.Ok(user.ToDto(RelationshipStatus.None));
    }

    private static void ApplyIdentity(User user, DiscordIdentity identity)
    {
        user.Username = identity.Username;
        user.GlobalName = identity.GlobalName;
        user.Discriminator = identity.Discriminator;
        user.AvatarHash = identity.AvatarHash;
    }

    private static IResult VerificationError(DiscordVerificationFailure failure) => failure switch
    {
        DiscordVerificationFailure.InvalidToken => ApiResults.Error(
            StatusCodes.Status401Unauthorized, "invalid_discord_token", "O Discord não confirmou este login. Conecte sua conta novamente."),
        DiscordVerificationFailure.NotConfigured => ApiResults.Error(
            StatusCodes.Status500InternalServerError, "server_not_configured", "O servidor não está configurado com o Client ID do Discord."),
        _ => ApiResults.Error(
            StatusCodes.Status503ServiceUnavailable, "discord_unavailable", "Não foi possível confirmar o login com o Discord agora. Tente novamente."),
    };
}
