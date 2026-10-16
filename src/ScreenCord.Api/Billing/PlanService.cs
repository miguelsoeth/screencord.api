using ScreenCord.Api.Data;
using ScreenCord.Api.Realtime;
using ScreenCord.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Billing;

public sealed class PlansOptions
{
    public const string SectionName = "Plans";

    /// <summary>Contas do Discord com Pro concedido à mão, sem prazo (testes, amigos). Não depende de pagamento.</summary>
    public List<string> ProDiscordIds { get; set; } = [];
}

/// <param name="ProUntilUtc">Até quando o Pro pago vale; null no gratuito ou no Pro concedido à mão.</param>
public sealed record PlanState(PlanTier Tier, DateTimeOffset? ProUntilUtc)
{
    public PlanLimits Limits => PlanLimits.For(Tier);
}

/// <summary>
/// Plano de cada conta: Pro enquanto houver período pago (<see cref="Subscription"/>) ou se a conta estiver em
/// <see cref="PlansOptions.ProDiscordIds"/>; senão, gratuito. Quando o Pro vence nada é apagado: grupos e
/// integrantes acima do limite continuam, só deixa de ser possível criar ou adicionar além dele.
/// </summary>
public sealed class PlanService(AppDbContext db, TimeProvider time, IOptions<PlansOptions> plans, IOptions<AbacatePayOptions> abacatePay)
{
    public async Task<PlanState> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (plans.Value.ProDiscordIds.Count > 0)
        {
            var discordId = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.DiscordId)
                .FirstOrDefaultAsync(cancellationToken);
            if (discordId is not null && plans.Value.ProDiscordIds.Contains(discordId))
            {
                return new PlanState(PlanTier.Pro, null);
            }
        }

        var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        return subscription is not null && subscription.ProUntilUtc > time.GetUtcNow()
            ? new PlanState(PlanTier.Pro, subscription.ProUntilUtc)
            : new PlanState(PlanTier.Free, null);
    }

    public async Task<PlanLimits> LimitsAsync(Guid userId, CancellationToken cancellationToken) =>
        (await GetAsync(userId, cancellationToken)).Limits;

    public async Task<PlanDto> GetDtoAsync(Guid userId, CancellationToken cancellationToken)
    {
        var state = await GetAsync(userId, cancellationToken);
        var options = abacatePay.Value;
        var offers = options.IsConfigured
            ? options.Offers.Select(o => new ProOfferDto(o.Id, o.Name, o.Days, o.PriceCents)).ToList()
            : [];
        return new PlanDto(state.Tier, state.ProUntilUtc, state.Limits, offers);
    }
}

/// <summary>Avisa todas as janelas abertas da conta de que o plano mudou.</summary>
public sealed class PlanNotifier(IHubContext<GroupsHub> hub)
{
    public Task PlanChangedAsync(Guid userId, PlanDto plan, CancellationToken cancellationToken = default) =>
        hub.Clients.User(userId.ToString()).SendAsync(GroupsHubContract.PlanChanged, plan, cancellationToken);
}
