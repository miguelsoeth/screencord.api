using System.Diagnostics;

namespace ScreenCord.Api.Observability;

/// <summary>
/// Leva para o painel os logs de erro da própria API (exceções não tratadas, falhas no hub e na mídia),
/// com o trace da requisição em que aconteceram.
/// </summary>
public sealed class ErrorLogProvider(ErrorSink sink, TimeProvider time) : ILoggerProvider
{
    // O EF Core registra as falhas do próprio gravador: capturá-las criaria um ciclo.
    private static readonly string[] IgnoredPrefixes = ["Microsoft.EntityFrameworkCore", "ScreenCord.Api.Observability"];

    public ILogger CreateLogger(string categoryName) =>
        IgnoredPrefixes.Any(prefix => categoryName.StartsWith(prefix, StringComparison.Ordinal))
            ? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            : new ErrorLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(ErrorEvent error) => sink.Enqueue(error);

    private DateTimeOffset Now => time.GetUtcNow();

    private sealed class ErrorLogger(ErrorLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Error or LogLevel.Critical;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = ErrorFingerprint.Truncate(formatter(state, exception), ErrorEvent.MaxMessageLength)!;
            var text = ErrorFingerprint.Truncate(exception?.ToString(), ErrorEvent.MaxExceptionLength);
            var activity = Activity.Current;
            var now = provider.Now;
            provider.Write(new ErrorEvent
            {
                Id = Guid.NewGuid(),
                Source = ErrorEvent.FromApi,
                OccurredAtUtc = now,
                ReceivedAtUtc = now,
                Level = logLevel.ToString(),
                Category = ErrorFingerprint.Truncate(category, 200)!,
                Message = message,
                Exception = text,
                TraceId = activity?.TraceId.ToHexString(),
                Fingerprint = ErrorFingerprint.Compute(ErrorEvent.FromApi, category, message, text),
                Request = ErrorFingerprint.Truncate(activity?.DisplayName, 300),
            });
        }
    }
}
