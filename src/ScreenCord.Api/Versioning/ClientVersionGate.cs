using ScreenCord.Api.Endpoints;
using ScreenCord.Contracts;

namespace ScreenCord.Api.Versioning;

/// <summary>Versões do aplicativo aceitas pela API (seção "Clients" do appsettings).</summary>
public sealed class ClientVersionOptions
{
    public const string SectionName = "Clients";

    /// <summary>
    /// Menor versão do aplicativo aceita. Suba quando a API deixar de atender versões antigas:
    /// elas recebem 426 e se atualizam sozinhas.
    /// </summary>
    public string MinVersion { get; set; } = "0.1.0";
}

/// <summary>
/// Lê a versão do aplicativo (<see cref="ClientVersionContract.Header"/>) e recusa as abaixo da mínima.
/// A versão fica disponível para os endpoints que precisam se comportar diferente por versão.
/// </summary>
public static class ClientVersionGate
{
    /// <summary>A v0.1.0 é a única publicada sem o cabeçalho: requisição sem ele vem dela.</summary>
    public static readonly Version Legacy = new(0, 1, 0);

    private static readonly object VersionKey = new();

    // Rotas que não são usadas pelo aplicativo (infra e documentação).
    private static readonly string[] ExemptPrefixes = ["/health", "/swagger", "/openapi"];

    /// <summary>Versão do aplicativo da requisição; null para build de desenvolvimento (aceita sempre).</summary>
    public static Version? GetClientVersion(this HttpContext context) => context.Items[VersionKey] as Version;

    /// <summary>
    /// Interpreta "MAJOR.MINOR.PATCH" (com sufixo opcional "-pre" ou "+meta"). Build de desenvolvimento
    /// (0.0.0-dev, código da main) devolve <paramref name="isDevBuild"/> true.
    /// </summary>
    public static bool TryParse(string? text, out Version version, out bool isDevBuild)
    {
        version = Legacy;
        isDevBuild = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v');
        var suffix = core.IndexOfAny(['-', '+']);
        var prerelease = suffix >= 0 && core[suffix] == '-';
        if (suffix >= 0)
        {
            core = core[..suffix];
        }

        var parts = core.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch) || major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new Version(major, minor, patch);
        isDevBuild = prerelease && version == new Version(0, 0, 0);
        return true;
    }

    public static Version ParseMinVersion(string text) =>
        TryParse(text, out var version, out var isDevBuild) && !isDevBuild
            ? version
            : throw new InvalidOperationException($"Clients:MinVersion inválida: \"{text}\". Use MAJOR.MINOR.PATCH.");

    public static IApplicationBuilder UseClientVersionGate(this IApplicationBuilder app, Version minVersion) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix)))
            {
                await next(context);
                return;
            }

            var header = context.Request.Headers[ClientVersionContract.Header].ToString();
            Version? version;
            if (header.Length == 0)
            {
                version = Legacy;
            }
            else if (TryParse(header, out var parsed, out var isDevBuild))
            {
                version = isDevBuild ? null : parsed;
            }
            else
            {
                await ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_client_version", "Versão do aplicativo inválida.")
                    .ExecuteAsync(context);
                return;
            }

            if (version is not null && version < minVersion)
            {
                // O texto aparece no aplicativo (inclusive na v0.1.0, que não sabe se atualizar sozinha).
                await ApiResults.Error(
                        StatusCodes.Status426UpgradeRequired,
                        ClientVersionContract.OutdatedCode,
                        $"Esta versão do ScreenCord ({version.ToString(3)}) não é mais aceita pelo servidor. Baixe a versão {minVersion.ToString(3)} ou mais nova em {ClientVersionContract.DownloadPage}")
                    .ExecuteAsync(context);
                return;
            }

            context.Items[VersionKey] = version;
            await next(context);
        });
}
