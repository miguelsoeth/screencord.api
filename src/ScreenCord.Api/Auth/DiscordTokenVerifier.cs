using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Auth;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>Application ID do aplicativo no Discord. Tokens emitidos para outros aplicativos são recusados.</summary>
    public string ClientId { get; set; } = "";
}

/// <summary>Dados da conta confirmados pelo Discord para um access token.</summary>
public sealed record DiscordIdentity(string Id, string Username, string? GlobalName, string? Discriminator, string? AvatarHash);

public enum DiscordVerificationFailure
{
    /// <summary>Token inválido, expirado, de outro aplicativo ou sem o escopo <c>identify</c>.</summary>
    InvalidToken,
    /// <summary>O Discord não respondeu ou respondeu com erro; não dá para decidir agora.</summary>
    Unavailable,
    /// <summary>O servidor não tem o Client ID configurado.</summary>
    NotConfigured,
}

public sealed class DiscordVerificationException(DiscordVerificationFailure failure, Exception? inner = null)
    : Exception($"Falha ao verificar o token do Discord: {failure}.", inner)
{
    public DiscordVerificationFailure Failure { get; } = failure;
}

public interface IDiscordTokenVerifier
{
    /// <summary>Confirma o token com o Discord e retorna a conta dona dele.</summary>
    Task<DiscordIdentity> VerifyAsync(string accessToken, CancellationToken cancellationToken);
}

/// <summary>
/// Usa <c>GET /oauth2/@me</c>, que informa para qual aplicativo o token foi emitido. Checar isso
/// impede que um token obtido por outro aplicativo do Discord seja usado para entrar como o usuário aqui.
/// </summary>
public sealed class DiscordTokenVerifier(HttpClient http, IOptions<DiscordOptions> options) : IDiscordTokenVerifier
{
    public async Task<DiscordIdentity> VerifyAsync(string accessToken, CancellationToken cancellationToken)
    {
        var clientId = options.Value.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new DiscordVerificationException(DiscordVerificationFailure.NotConfigured);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "oauth2/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        AuthorizationInfo? info;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new DiscordVerificationException(DiscordVerificationFailure.InvalidToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DiscordVerificationException(DiscordVerificationFailure.Unavailable);
            }

            info = await response.Content.ReadFromJsonAsync<AuthorizationInfo>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new DiscordVerificationException(DiscordVerificationFailure.Unavailable, ex);
        }

        var user = info?.User;
        if (info?.Application?.Id != clientId ||
            info.Scopes?.Contains("identify") != true ||
            user is null ||
            !ulong.TryParse(user.Id, out _) ||
            string.IsNullOrWhiteSpace(user.Username) ||
            user.Username.Length > 64 ||
            (user.Avatar is not null && !IsValidAvatarHash(user.Avatar)))
        {
            throw new DiscordVerificationException(DiscordVerificationFailure.InvalidToken);
        }

        return new DiscordIdentity(
            user.Id!,
            user.Username,
            string.IsNullOrWhiteSpace(user.GlobalName) ? null : Truncate(user.GlobalName, 64),
            user.Discriminator is { Length: <= 4 } ? user.Discriminator : null,
            user.Avatar);
    }

    private static bool IsValidAvatarHash(string hash)
    {
        var span = hash.AsSpan();
        if (span.StartsWith("a_"))
        {
            span = span[2..];
        }

        return span.Length is > 0 and <= 64 && !span.ContainsAnyExcept("0123456789abcdefABCDEF");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record AuthorizationInfo(
        [property: JsonPropertyName("application")] ApplicationInfo? Application,
        [property: JsonPropertyName("scopes")] string[]? Scopes,
        [property: JsonPropertyName("user")] UserInfo? User);

    private sealed record ApplicationInfo([property: JsonPropertyName("id")] string? Id);

    private sealed record UserInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("discriminator")] string? Discriminator,
        [property: JsonPropertyName("avatar")] string? Avatar);
}
