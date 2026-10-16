using System.Text.Json;
using ScreenCord.Api.Auth;
using ScreenCord.Api.Billing;
using ScreenCord.Api.Data;
using ScreenCord.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Endpoints;

/// <summary>
/// Plano da conta e compra de períodos de Pro pelo AbacatePay. O pagamento é único (Pix, por padrão) e soma
/// dias ao Pro; não há renovação automática. O Pro só é ativado pelo webhook, nunca pelo retorno do navegador.
/// </summary>
public static class PlanEndpoints
{
    public const string CheckoutRateLimitPolicy = "checkout";

    public const string WebhookPath = "/billing/abacatepay/webhook";

    private const int MaxWebhookBytes = 256 * 1024;

    public static void MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me/plan", GetPlanAsync)
            .RequireAuthorization()
            .WithTags("Plano")
            .WithSummary("Plano da conta, limites e períodos de Pro à venda")
            .Produces<PlanDto>();

        app.MapPost("/billing/checkout", CreateCheckoutAsync)
            .RequireAuthorization()
            .RequireRateLimiting(CheckoutRateLimitPolicy)
            .WithTags("Plano")
            .WithSummary("Abre o pagamento de um período de Pro")
            .WithDescription("Devolve a URL do checkout do AbacatePay. Quando o pagamento é confirmado, o Pro é ativado e o hub envia PlanChanged.")
            .Produces<ProCheckoutDto>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost(WebhookPath, HandleWebhookAsync)
            .WithTags("Plano")
            .WithSummary("Webhook do AbacatePay")
            .WithDescription("Autenticado pelo parâmetro webhookSecret e pela assinatura X-Webhook-Signature. Trata checkout.completed, checkout.refunded e checkout.disputed.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> GetPlanAsync(HttpContext context, PlanService plans, CancellationToken cancellationToken) =>
        Results.Ok(await plans.GetDtoAsync(context.User.GetUserId(), cancellationToken));

    private static async Task<IResult> CreateCheckoutAsync(
        ProCheckoutCreate request,
        HttpContext context,
        AppDbContext db,
        IAbacatePayClient abacatePay,
        IOptions<AbacatePayOptions> options,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "billing_unavailable", "A assinatura do Pro ainda não está disponível.");
        }

        var offer = settings.Offers.FirstOrDefault(o => o.Id == request.OfferId);
        if (offer is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_offer", "Este período do Pro não está à venda.");
        }

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            UserId = context.User.GetUserId(),
            OfferId = offer.Id,
            Days = offer.Days,
            AmountCents = offer.PriceCents,
            Status = PaymentStatus.Pending,
            CreatedAtUtc = time.GetUtcNow(),
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);

        AbacatePayCheckout checkout;
        try
        {
            checkout = await abacatePay.CreateCheckoutAsync(offer.ProductId, payment.Id.ToString(), cancellationToken);
        }
        catch (AbacatePayException ex)
        {
            loggerFactory.CreateLogger("Billing").LogError(ex, "Falha ao criar o checkout do pagamento {PaymentId}.", payment.Id);
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "billing_unavailable", "Não foi possível abrir o pagamento agora. Tente novamente.");
        }

        // O aplicativo abre a URL no navegador: só aceita HTTPS.
        if (!Uri.TryCreate(checkout.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            loggerFactory.CreateLogger("Billing").LogError("Checkout {CheckoutId} veio com uma URL inválida.", checkout.Id);
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "billing_unavailable", "Não foi possível abrir o pagamento agora. Tente novamente.");
        }

        payment.CheckoutId = checkout.Id;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new ProCheckoutDto(url.ToString()));
    }

    /// <summary>
    /// Responde 2xx a tudo que foi autenticado, mesmo quando o evento é ignorado: outro status faria o
    /// AbacatePay tentar de novo (5xx) ou desistir do webhook (410). Entregas repetidas não somam dias duas vezes.
    /// </summary>
    private static async Task<IResult> HandleWebhookAsync(
        HttpContext context,
        AppDbContext db,
        PlanService plans,
        PlanNotifier notifier,
        IOptions<AbacatePayOptions> options,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Billing");
        if (context.Request.ContentLength > MaxWebhookBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxWebhookBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = buffer.ToArray();
        var settings = options.Value;
        if (!AbacatePayWebhook.IsAuthentic(
                settings,
                context.Request.Query["webhookSecret"].FirstOrDefault(),
                context.Request.Headers[AbacatePayWebhook.SignatureHeader].FirstOrDefault(),
                body))
        {
            logger.LogWarning("Webhook do AbacatePay recusado: secret ou assinatura não conferem.");
            return Results.Unauthorized();
        }

        WebhookEvent? webhook;
        try
        {
            webhook = WebhookEvent.Parse(body);
        }
        catch (JsonException)
        {
            webhook = null;
        }

        if (webhook is null)
        {
            logger.LogWarning("Webhook do AbacatePay sem o formato esperado; ignorado.");
            return Results.Ok();
        }

        if (webhook.DevMode != settings.DevMode)
        {
            logger.LogWarning("Evento {Event} de outro ambiente (devMode={DevMode}) ignorado.", webhook.Event, webhook.DevMode);
            return Results.Ok();
        }

        var change = webhook.Event switch
        {
            "checkout.completed" when webhook.Status == "PAID" => PaymentChange.Paid,
            "checkout.refunded" or "checkout.disputed" => PaymentChange.Refunded,
            _ => (PaymentChange?)null,
        };
        if (change is null)
        {
            return Results.Ok();
        }

        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.CheckoutId == webhook.CheckoutId, cancellationToken);
        if (payment is null || (webhook.ExternalId is { } externalId && externalId != payment.Id.ToString()))
        {
            logger.LogWarning("Evento {Event} do checkout {CheckoutId} sem pagamento correspondente; ignorado.", webhook.Event, webhook.CheckoutId);
            return Results.Ok();
        }

        if (await ApplyAsync(db, payment, change.Value, time.GetUtcNow(), cancellationToken))
        {
            logger.LogInformation("Pagamento {PaymentId} ({Days} dias de Pro): {Change}.", payment.Id, payment.Days, change);
            await notifier.PlanChangedAsync(payment.UserId, await plans.GetDtoAsync(payment.UserId, cancellationToken), cancellationToken);
        }

        return Results.Ok();
    }

    private enum PaymentChange
    {
        Paid,
        Refunded,
    }

    /// <summary>
    /// Muda o status do pagamento e soma (ou tira) os dias do Pro numa transação. A troca de status só acontece
    /// a partir do status esperado, então uma entrega repetida ou simultânea não tem efeito. Retorna se mudou algo.
    /// </summary>
    private static async Task<bool> ApplyAsync(AppDbContext db, Payment payment, PaymentChange change, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = change == PaymentChange.Paid
            ? await db.Payments
                .Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Paid).SetProperty(p => p.PaidAtUtc, now), cancellationToken)
            : await db.Payments
                .Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Paid)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Refunded).SetProperty(p => p.RefundedAtUtc, now), cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == payment.UserId, cancellationToken);
        if (subscription is null)
        {
            subscription = new Subscription { UserId = payment.UserId, ProUntilUtc = now };
            db.Subscriptions.Add(subscription);
        }

        // Comprar com Pro ainda valendo emenda o período novo no fim do atual.
        subscription.ProUntilUtc = change == PaymentChange.Paid
            ? Max(subscription.ProUntilUtc, now).AddDays(payment.Days)
            : Max(subscription.ProUntilUtc.AddDays(-payment.Days), now);
        subscription.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>Só os campos usados; o resto do payload é ignorado (o AbacatePay pede para não validar o corpo inteiro).</summary>
    private sealed record WebhookEvent(string Event, bool DevMode, string CheckoutId, string? ExternalId, string? Status)
    {
        public static WebhookEvent? Parse(byte[] body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("event", out var name) || name.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("checkout", out var checkout) || checkout.ValueKind != JsonValueKind.Object ||
                !checkout.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var devMode = root.TryGetProperty("devMode", out var mode) && mode.ValueKind == JsonValueKind.True;
            return new WebhookEvent(name.GetString()!, devMode, id.GetString()!, Text(checkout, "externalId"), Text(checkout, "status"));
        }

        private static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
