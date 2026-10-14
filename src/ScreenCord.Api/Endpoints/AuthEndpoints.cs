using ScreenCord.Api.Auth;
using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Endpoints;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/discord", LoginWithDiscordAsync).RequireRateLimiting(LoginRateLimitPolicy);
        app.MapPost("/auth/logout", LogoutAsync).RequireAuthorization();
        app.MapGet("/me", GetMeAsync).RequireAuthorization();
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
            return ex.Failure switch
            {
                DiscordVerificationFailure.InvalidToken => ApiResults.Error(
                    StatusCodes.Status401Unauthorized, "invalid_discord_token", "O Discord não confirmou este login. Conecte sua conta novamente."),
                DiscordVerificationFailure.NotConfigured => ApiResults.Error(
                    StatusCodes.Status500InternalServerError, "server_not_configured", "O servidor não está configurado com o Client ID do Discord."),
                _ => ApiResults.Error(
                    StatusCodes.Status503ServiceUnavailable, "discord_unavailable", "Não foi possível confirmar o login com o Discord agora. Tente novamente."),
            };
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

        user.Username = identity.Username;
        user.GlobalName = identity.GlobalName;
        user.Discriminator = identity.Discriminator;
        user.AvatarHash = identity.AvatarHash;
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
}
