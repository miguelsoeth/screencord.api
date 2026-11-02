using System.Net;

namespace ScreenCord.Api.Observability;

/// <summary>
/// Tentativas com token do painel errado por endereço: depois de <see cref="MaxFailures"/> em
/// <see cref="FailureWindow"/>, o endereço fica <see cref="BlockDuration"/> sem poder tentar (nem com o token certo).
/// </summary>
public sealed class AdminAuthGuard(TimeProvider time)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);

    /// <summary>Endereços guardados no máximo; acima disso os vencidos saem (muitos endereços tentando não esgotam a memória).</summary>
    private const int MaxTracked = 4096;

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];

    public bool IsBlocked(IPAddress address)
    {
        if (address is null)
        {
            return false;
        }

        lock (_lock)
        {
            return _entries.TryGetValue(address, out var entry) && entry.BlockedUntil > time.GetUtcNow();
        }
    }

    /// <summary>Conta uma tentativa com token errado. Retorna true se o endereço acabou de ser bloqueado.</summary>
    public bool RecordFailure(IPAddress address)
    {
        if (address is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_entries.Count >= MaxTracked)
            {
                foreach (var stale in _entries.Where(e => e.Value.BlockedUntil <= now && now - e.Value.WindowStart > FailureWindow).Select(e => e.Key).ToList())
                {
                    _entries.Remove(stale);
                }
            }

            if (!_entries.TryGetValue(address, out var entry) || (entry.BlockedUntil <= now && now - entry.WindowStart > FailureWindow))
            {
                entry = new Entry(now, 0, DateTimeOffset.MinValue);
            }

            entry = entry with { Failures = entry.Failures + 1 };
            var blocked = entry.Failures >= MaxFailures;
            if (blocked)
            {
                // Recomeça a contagem: quando o bloqueio vence, são mais dez tentativas até o próximo.
                entry = new Entry(now, 0, now + BlockDuration);
            }

            if (_entries.Count < MaxTracked || _entries.ContainsKey(address))
            {
                _entries[address] = entry;
            }

            return blocked;
        }
    }

    /// <summary>Acerto: zera a contagem de erros do endereço.</summary>
    public void RecordSuccess(IPAddress address)
    {
        if (address is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_entries.TryGetValue(address, out var entry) && entry.BlockedUntil <= time.GetUtcNow())
            {
                _entries.Remove(address);
            }
        }
    }

    private sealed record Entry(DateTimeOffset WindowStart, int Failures, DateTimeOffset BlockedUntil);
}
