using System.Collections.Concurrent;
using System.Security.Claims;
using ScreenCord.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ScreenCord.Api.Observability;

/// <summary>Um usuário que chamou a API num dia (UTC). Uma linha por usuário e dia: é a base dos usuários ativos do painel.</summary>
public sealed class UserActivity
{
    public Guid UserId { get; set; }

    public DateOnly Day { get; set; }
}

/// <summary>
/// Usuários ativos: grava o dia de uso de cada conta na primeira requisição autenticada do dia.
/// Quem usa sem conta (só salas locais) não fala com a API e não entra na conta.
/// </summary>
public sealed class ActiveUsers(IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>Dias guardados; o painel mostra até 30.</summary>
    public const int RetentionDays = 90;

    // Último dia já gravado de cada usuário, para não ir ao banco a cada requisição.
    private readonly ConcurrentDictionary<Guid, DateOnly> _recorded = new();

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    public async Task RecordAsync(Guid userId, CancellationToken cancellationToken)
    {
        var today = Today;
        if (_recorded.TryGetValue(userId, out var day) && day == today)
        {
            return;
        }

        // Escopo próprio: uma falha aqui não pode deixar uma entidade pendente no contexto da requisição.
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.UserActivities.AnyAsync(a => a.UserId == userId && a.Day == today, cancellationToken))
        {
            db.UserActivities.Add(new UserActivity { UserId = userId, Day = today });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Outra requisição do mesmo usuário gravou o dia primeiro.
            }
        }

        _recorded[userId] = today;
    }

    public async Task<object> SummaryAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var today = Today;
        var monthStart = today.AddDays(-29);
        var weekStart = today.AddDays(-6);
        var daily = await db.UserActivities.Where(a => a.Day >= monthStart)
            .GroupBy(a => a.Day).Select(g => new { day = g.Key, users = g.Count() }).OrderBy(d => d.day).ToListAsync(cancellationToken);

        return new
        {
            today = daily.FirstOrDefault(d => d.day == today)?.users ?? 0,
            last7Days = await db.UserActivities.Where(a => a.Day >= weekStart).Select(a => a.UserId).Distinct().CountAsync(cancellationToken),
            last30Days = await db.UserActivities.Where(a => a.Day >= monthStart).Select(a => a.UserId).Distinct().CountAsync(cancellationToken),
            accounts = await db.Users.CountAsync(cancellationToken),
            daily,
        };
    }

    public static Task CleanupAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-RetentionDays);
        return db.UserActivities.Where(a => a.Day < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}

public static class ActiveUsersMiddleware
{
    /// <summary>Depois da autenticação: conta o usuário da requisição (inclusive a conexão do hub e da mídia).</summary>
    public static IApplicationBuilder UseActiveUsers(this IApplicationBuilder app)
    {
        var activeUsers = app.ApplicationServices.GetRequiredService<ActiveUsers>();
        var logger = app.ApplicationServices.GetRequiredService<ILogger<ActiveUsers>>();

        return app.Use(async (context, next) =>
        {
            if (Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                try
                {
                    await activeUsers.RecordAsync(userId, context.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A métrica não pode derrubar a requisição.
                    logger.LogWarning(ex, "Falha ao registrar o usuário ativo.");
                }
            }

            await next(context);
        });
    }
}
