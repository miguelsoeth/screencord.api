using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenCord.Api.Observability;

/// <summary>Erro registrado para o painel: enviado pelo aplicativo ou da própria API.</summary>
public sealed class ErrorEvent
{
    public const string FromApp = "app";
    public const string FromApi = "api";

    public const int MaxMessageLength = 4000;
    public const int MaxExceptionLength = 16000;

    public Guid Id { get; set; }

    /// <summary><see cref="FromApp"/> ou <see cref="FromApi"/>.</summary>
    public required string Source { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public required string Level { get; set; }

    public required string Category { get; set; }

    public required string Message { get; set; }

    public string? Exception { get; set; }

    /// <summary>Trace W3C (32 hex): liga o erro do aplicativo à requisição que falhou na API.</summary>
    public string? TraceId { get; set; }

    /// <summary>Agrupa ocorrências do mesmo problema (<see cref="ErrorFingerprint"/>).</summary>
    public required string Fingerprint { get; set; }

    /// <summary>Versão do aplicativo (no erro da API, a de quem fez a requisição).</summary>
    public string? AppVersion { get; set; }

    public string? Os { get; set; }

    public string? Environment { get; set; }

    public Guid? InstallId { get; set; }

    public Guid? RunId { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>Requisição em que o erro aconteceu na API (ex.: <c>POST /groups → 500 (12 ms)</c>).</summary>
    public string? Request { get; set; }
}

/// <summary>
/// Identifica o "mesmo problema" entre ocorrências: o tipo da exceção e os primeiros quadros da pilha
/// (sem arquivo e linha, que mudam entre versões) ou, sem exceção, o texto sem números e ids.
/// </summary>
public static partial class ErrorFingerprint
{
    public static string Compute(string source, string category, string message, string? exception)
    {
        var key = new StringBuilder(source).Append('|').Append(category).Append('|');
        if (!string.IsNullOrWhiteSpace(exception))
        {
            var lines = exception.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var type = lines[0].Split(':', 2)[0];
            key.Append(type);
            foreach (var frame in lines.Where(l => l.StartsWith("at ", StringComparison.Ordinal)).Take(3))
            {
                key.Append('|').Append(FileAndLine().Replace(frame, ""));
            }
        }
        else
        {
            key.Append(Variable().Replace(message, "#"));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..16].ToLowerInvariant();
    }

    public static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(@" in .*$")]
    private static partial Regex FileAndLine();

    // Guids, hexadecimais longos e números: mudam a cada ocorrência.
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}|\b[0-9a-fA-F]{16,}\b|\d+")]
    private static partial Regex Variable();
}
