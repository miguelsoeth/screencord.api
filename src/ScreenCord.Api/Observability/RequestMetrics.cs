namespace ScreenCord.Api.Observability;

/// <summary>
/// Métricas das requisições em memória, por minuto e por rota, das últimas 24 horas. Recomeçam a cada deploy
/// (a API roda numa instância só); o que precisa sobreviver, os erros, vai para o banco.
/// </summary>
public sealed class RequestMetrics(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Limites superiores (ms) das faixas de duração; a última faixa é "acima de".</summary>
    public static readonly double[] Bounds = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000];

    private readonly Lock _lock = new();
    private readonly Dictionary<(long Minute, string Route), RouteStats> _buckets = [];
    private long _prunedAt;
    private int _activeHub;
    private int _activeMedia;

    public DateTimeOffset StartedAtUtc { get; } = time.GetUtcNow();

    public int ActiveHubConnections => Volatile.Read(ref _activeHub);

    public int ActiveMediaConnections => Volatile.Read(ref _activeMedia);

    public void Record(string route, int status, double milliseconds, bool longLived)
    {
        var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        lock (_lock)
        {
            if (!_buckets.TryGetValue((minute, route), out var stats))
            {
                stats = new RouteStats();
                _buckets[(minute, route)] = stats;
            }

            stats.Count++;
            if (status >= 500)
            {
                stats.ServerErrors++;
            }
            else if (status >= 400)
            {
                stats.ClientErrors++;
            }

            // Conexões do hub e da mídia duram minutos ou horas: contam, mas não entram na latência.
            if (!longLived)
            {
                stats.Timed++;
                stats.TotalMs += milliseconds;
                stats.MaxMs = Math.Max(stats.MaxMs, milliseconds);
                var bucket = Array.FindIndex(Bounds, bound => milliseconds <= bound);
                stats.Histogram[bucket < 0 ? Bounds.Length : bucket]++;
            }

            if (minute != _prunedAt)
            {
                _prunedAt = minute;
                var oldest = minute - (long)Window.TotalMinutes;
                foreach (var key in _buckets.Keys.Where(k => k.Minute < oldest).ToList())
                {
                    _buckets.Remove(key);
                }
            }
        }
    }

    public MetricsSnapshot Snapshot(TimeSpan period)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        var first = now - (long)Math.Min(period.TotalMinutes, Window.TotalMinutes) + 1;
        var timeline = new Dictionary<long, TimelinePoint>();
        var routes = new Dictionary<string, RouteStats>();
        var total = new RouteStats();
        lock (_lock)
        {
            foreach (var ((minute, route), stats) in _buckets.Where(b => b.Key.Minute >= first))
            {
                var point = timeline.GetValueOrDefault(minute) ?? new TimelinePoint(DateTimeOffset.FromUnixTimeSeconds(minute * 60), 0, 0, 0);
                timeline[minute] = point with
                {
                    Requests = point.Requests + stats.Count,
                    ClientErrors = point.ClientErrors + stats.ClientErrors,
                    ServerErrors = point.ServerErrors + stats.ServerErrors,
                };
                routes[route] = routes.GetValueOrDefault(route, new RouteStats()).Add(stats);
                total.Add(stats);
            }
        }

        return new MetricsSnapshot(
            Summary("total", total),
            routes.Select(r => Summary(r.Key, r.Value)).OrderByDescending(r => r.ServerErrors).ThenByDescending(r => r.Requests).ToList(),
            timeline.Values.OrderBy(p => p.MinuteUtc).ToList());
    }

    private static RouteSummary Summary(string route, RouteStats stats) => new(
        route,
        stats.Count,
        stats.ClientErrors,
        stats.ServerErrors,
        stats.Timed == 0 ? 0 : Math.Round(stats.TotalMs / stats.Timed, 1),
        Percentile(stats, 0.5),
        Percentile(stats, 0.95),
        Math.Round(stats.MaxMs, 1));

    // Estimado pela faixa: devolve o limite superior da faixa em que o percentil cai (nunca acima do máximo).
    private static double Percentile(RouteStats stats, double p)
    {
        if (stats.Timed == 0)
        {
            return 0;
        }

        var target = Math.Ceiling(stats.Timed * p);
        var seen = 0;
        for (var i = 0; i < stats.Histogram.Length; i++)
        {
            seen += stats.Histogram[i];
            if (seen >= target)
            {
                return Math.Round(i < Bounds.Length ? Math.Min(Bounds[i], stats.MaxMs) : stats.MaxMs, 1);
            }
        }

        return Math.Round(stats.MaxMs, 1);
    }

    public void ConnectionOpened(bool hub) => Interlocked.Increment(ref hub ? ref _activeHub : ref _activeMedia);

    public void ConnectionClosed(bool hub) => Interlocked.Decrement(ref hub ? ref _activeHub : ref _activeMedia);

    private sealed class RouteStats
    {
        public int Count;
        public int ClientErrors;
        public int ServerErrors;
        public int Timed;
        public double TotalMs;
        public double MaxMs;
        public int[] Histogram = new int[Bounds.Length + 1];

        public RouteStats Add(RouteStats other)
        {
            Count += other.Count;
            ClientErrors += other.ClientErrors;
            ServerErrors += other.ServerErrors;
            Timed += other.Timed;
            TotalMs += other.TotalMs;
            MaxMs = Math.Max(MaxMs, other.MaxMs);
            for (var i = 0; i < Histogram.Length; i++)
            {
                Histogram[i] += other.Histogram[i];
            }

            return this;
        }
    }
}

public sealed record RouteSummary(string Route, int Requests, int ClientErrors, int ServerErrors, double AvgMs, double P50Ms, double P95Ms, double MaxMs);

public sealed record TimelinePoint(DateTimeOffset MinuteUtc, int Requests, int ClientErrors, int ServerErrors);

public sealed record MetricsSnapshot(RouteSummary Total, IReadOnlyList<RouteSummary> Routes, IReadOnlyList<TimelinePoint> Timeline);
