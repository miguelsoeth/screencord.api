using System.Threading.Channels;
using ScreenCord.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ScreenCord.Api.Observability;

/// <summary>Configuração do painel e da coleta de erros (seção "Observability" do appsettings).</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Token do painel (<c>/admin</c>). Vazio desliga o painel. Em produção fica no <c>api.env</c>.</summary>
    public string DashboardToken { get; set; } = "";

    public int RetentionDays { get; set; } = 30;

    /// <summary>Teto de erros guardados; acima dele os mais antigos saem primeiro.</summary>
    public int MaxEvents { get; set; } = 50_000;
}

/// <summary>
/// Grava em segundo plano os erros da própria API (vindos do log) e apaga os antigos. A fila é limitada:
/// numa avalanche de erros, os excedentes são descartados em vez de segurar memória.
/// </summary>
public sealed class ErrorSink(IServiceScopeFactory scopes, IOptions<ObservabilityOptions> options, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly Channel<ErrorEvent> _queue = Channel.CreateBounded<ErrorEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

    public void Enqueue(ErrorEvent error) => _queue.Writer.TryWrite(error);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ErrorEvent>();
        // A primeira limpeza fica para depois da subida, que já tem trabalho de sobra.
        _nextCleanup = time.GetUtcNow().AddMinutes(1);
        while (true)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(CleanupInterval);
                try
                {
                    await _queue.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }

                while (batch.Count < 200 && _queue.Reader.TryRead(out var error))
                {
                    batch.Add(error);
                }

                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (batch.Count > 0)
                {
                    db.ErrorEvents.AddRange(batch);
                    await db.SaveChangesAsync(stoppingToken);
                }

                if (time.GetUtcNow() >= _nextCleanup)
                {
                    await CleanupAsync(db, options.Value, time.GetUtcNow(), stoppingToken);
                    _nextCleanup = time.GetUtcNow() + CleanupInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Não passa pelo ILogger: o erro voltaria para esta mesma fila.
                Console.Error.WriteLine($"Falha ao gravar os erros do painel: {ex.Message}");
            }
            finally
            {
                batch.Clear();
            }
        }
    }

    public static async Task CleanupAsync(AppDbContext db, ObservabilityOptions options, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-Math.Max(1, options.RetentionDays));
        await db.ErrorEvents.Where(e => e.OccurredAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken);

        var newestKept = await db.ErrorEvents.OrderByDescending(e => e.OccurredAtUtc)
            .Skip(Math.Max(1, options.MaxEvents)).Select(e => (DateTimeOffset?)e.OccurredAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (newestKept is { } limit)
        {
            await db.ErrorEvents.Where(e => e.OccurredAtUtc <= limit).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
