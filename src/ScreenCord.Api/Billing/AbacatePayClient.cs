using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Billing;

public sealed class AbacatePayOptions
{
    public const string SectionName = "AbacatePay";

    /// <summary>
    /// Chave HMAC pública que o AbacatePay usa para assinar o corpo dos webhooks (<c>X-Webhook-Signature</c>).
    /// É a mesma para todas as lojas e está na documentação deles; não é segredo.
    /// </summary>
    public const string DefaultSignatureKey =
        "t9dXRhHHo3yDEj5pVDYz0frf7q6bMKyMRmxxCPIPp3RCplBfXRxqlC6ZpiWmOqj4L63qEaeUOtrCI8P0VMUgo6iIga2ri9ogaHFs0WIIywSMg0q7RmBfybe1E5XJcfC4IW3alNqym0tXoAKkzvfEjZxV6bE0oG2zJrNNYmUCKZyV0KZ3JS8Votf9EAWWYdiDkMkpbMdPggfh1EqHlVkMiTady6jOR3hyzGEHrIz2Ret0xHKMbiqkr9HS1JhNHDX9";

    /// <summary>Chave de API (<c>abc_...</c>) com a permissão <c>CHECKOUT:CREATE</c>. A chave define o ambiente: Dev mode ou produção.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Secret do webhook cadastrado no AbacatePay; chega em cada entrega no parâmetro <c>webhookSecret</c> da URL.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>A chave de API é de Dev mode. Eventos de outro ambiente que cheguem ao webhook são ignorados.</summary>
    public bool DevMode { get; set; }

    public string SignatureKey { get; set; } = DefaultSignatureKey;

    /// <summary>Para onde o navegador vai depois do pagamento.</summary>
    public string? CompletionUrl { get; set; }

    /// <summary>Formas de pagamento aceitas no checkout (<c>PIX</c>, <c>CARD</c>, <c>BOLETO</c>).</summary>
    public List<string> Methods { get; set; } = [];

    /// <summary>Períodos de Pro à venda. O preço cobrado é o do produto no AbacatePay; <see cref="ProOffer.PriceCents"/> só é exibido.</summary>
    public List<ProOffer> Offers { get; set; } = [];

    public bool IsConfigured => ApiKey.Length > 0 && WebhookSecret.Length > 0 && Offers.Count > 0;

    public IReadOnlyList<string> CheckoutMethods => Methods.Count > 0 ? Methods : ["PIX"];
}

public sealed class ProOffer
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public int Days { get; set; }

    public int PriceCents { get; set; }

    /// <summary>Produto avulso (sem <c>cycle</c>) no AbacatePay (<c>prod_...</c>).</summary>
    public string ProductId { get; set; } = "";
}

public sealed record AbacatePayCheckout(string Id, string Url);

public sealed class AbacatePayException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAbacatePayClient
{
    /// <summary>Cria o checkout hospedado de um produto. <paramref name="externalId"/> volta no webhook.</summary>
    Task<AbacatePayCheckout> CreateCheckoutAsync(string productId, string externalId, CancellationToken cancellationToken);
}

/// <summary>Cliente da API v2 do AbacatePay (<c>https://api.abacatepay.com/v2/</c>).</summary>
public sealed class AbacatePayClient(HttpClient http, IOptions<AbacatePayOptions> options) : IAbacatePayClient
{
    public async Task<AbacatePayCheckout> CreateCheckoutAsync(string productId, string externalId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "checkouts/create")
        {
            Content = JsonContent.Create(new CheckoutRequest(
                [new CheckoutItem(productId, 1)],
                settings.CheckoutMethods,
                externalId,
                string.IsNullOrWhiteSpace(settings.CompletionUrl) ? null : settings.CompletionUrl)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<Envelope>(cancellationToken);
            if (!response.IsSuccessStatusCode || body?.Data is not { Id.Length: > 0, Url.Length: > 0 } data)
            {
                throw new AbacatePayException($"Checkout recusado ({(int)response.StatusCode}): {body?.Error ?? "sem detalhes"}.");
            }

            return new AbacatePayCheckout(data.Id, data.Url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new AbacatePayException("O AbacatePay não respondeu.", ex);
        }
    }

    private sealed record CheckoutItem(string Id, int Quantity);

    private sealed record CheckoutRequest(
        IReadOnlyList<CheckoutItem> Items,
        IReadOnlyList<string> Methods,
        string ExternalId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompletionUrl);

    private sealed record Envelope(CheckoutData? Data, string? Error);

    private sealed record CheckoutData(string? Id, string? Url);
}

public static class AbacatePayWebhook
{
    public const string SignatureHeader = "X-Webhook-Signature";

    /// <summary>
    /// Confere o secret da URL (prova que veio do AbacatePay) e a assinatura HMAC-SHA256 do corpo, em base64
    /// (prova que o corpo não foi alterado).
    /// </summary>
    public static bool IsAuthentic(AbacatePayOptions options, string? secret, string? signature, ReadOnlySpan<byte> body)
    {
        if (options.WebhookSecret.Length == 0 || secret is null || signature is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(options.WebhookSecret)))
        {
            return false;
        }

        var expected = Sign(options.SignatureKey, body);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected));
    }

    public static string Sign(string key, ReadOnlySpan<byte> body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body));
}
