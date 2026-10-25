using System.Runtime.CompilerServices;

namespace ScreenCord.Contracts;

/// <summary>O que aconteceu com um pacote de vídeo oferecido à <see cref="MediaQueue"/>.</summary>
public enum MediaOffer
{
    Queued,

    /// <summary>Descartado: a fila está atrasada ou espera um quadro-chave.</summary>
    Dropped,

    /// <summary>Descartado, e a fila já esvaziou: é hora de pedir um quadro-chave para voltar.</summary>
    NeedsKeyframe,
}

/// <summary>
/// Fila de saída de uma conexão de mídia (quem compartilha → API, API → quem assiste, sala local → quem assiste).
/// O limite é de tempo: se o pacote mais antigo ainda não entregue espera mais que <see cref="MaxDelay"/>, a rede não
/// está acompanhando, e o vídeo é descartado até o próximo quadro-chave em vez de chegar cada vez mais atrasado.
/// O quadro-chave só é pedido depois que a fila esvazia, para não ficar preso atrás do atraso.
/// </summary>
/// <remarks>
/// Um quadro-chave é bem maior que os outros e leva mais tempo para sair: enquanto ele é enviado, a espera não conta,
/// e quem estava atrás dele conta a partir de quando ele terminou. O limite de bytes é só uma proteção extra.
/// Existe uma cópia idêntica no repositório da API.
/// </remarks>
public sealed class MediaQueue
{
    /// <summary>Atraso máximo de cada fila. Somado nas duas pontas (quem compartilha e o servidor), fica abaixo de 1 s.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMilliseconds(400);

    private readonly Lock _lock = new();
    private readonly Queue<Entry> _entries = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly TimeProvider _time;
    private Entry? _sending;
    private long _keyframeSentAt;
    private long _queuedBytes;
    private long _overflows;
    private bool _waitingForKeyframe;
    private bool _completed;

    /// <param name="waitForKeyframe">Começa descartando até o primeiro quadro-chave (quem acabou de entrar para assistir).</param>
    public MediaQueue(int bitrateKbps, TimeProvider? time = null, TimeSpan? maxDelay = null, bool waitForKeyframe = false)
    {
        _time = time ?? TimeProvider.System;
        MaxDelay = maxDelay ?? DefaultMaxDelay;

        // Cerca de 1 s de vídeo (no mínimo 512 KB, para caber um quadro-chave).
        MaxBytes = Math.Max(512 * 1024, bitrateKbps * 1000L / 8);
        _waitingForKeyframe = waitForKeyframe;
    }

    public TimeSpan MaxDelay { get; }

    public long MaxBytes { get; }

    /// <summary>Bytes ainda não entregues ao socket (inclui o pacote sendo enviado).</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>Quantas vezes a fila atrasou demais e o vídeo passou a ser descartado até o próximo quadro-chave.</summary>
    public long Overflows => Interlocked.Read(ref _overflows);

    /// <summary>Há quanto tempo o pacote mais antigo espera (sem contar o envio de um quadro-chave).</summary>
    public TimeSpan Delay
    {
        get
        {
            lock (_lock)
            {
                return DelayLocked();
            }
        }
    }

    /// <summary>Oferece um pacote de vídeo (já com o cabeçalho).</summary>
    public MediaOffer OfferVideo(byte[] packet, bool keyframe)
    {
        lock (_lock)
        {
            if (_completed)
            {
                return MediaOffer.Dropped;
            }

            var delay = DelayLocked();
            var fits = _queuedBytes + packet.Length <= MaxBytes;
            if (_waitingForKeyframe)
            {
                var drained = delay < MaxDelay / 2 && fits;
                if (!keyframe || !drained)
                {
                    return drained ? MediaOffer.NeedsKeyframe : MediaOffer.Dropped;
                }
            }
            else if (!fits || delay > MaxDelay)
            {
                _waitingForKeyframe = true;
                _overflows++;
                return MediaOffer.Dropped;
            }

            if (keyframe)
            {
                _waitingForKeyframe = false;
            }

            EnqueueLocked(packet, keyframe);
            return MediaOffer.Queued;
        }
    }

    /// <summary>Oferece um pacote de áudio: vai junto enquanto a fila não estiver muito atrasada.</summary>
    public bool OfferAudio(byte[] packet)
    {
        lock (_lock)
        {
            if (_completed || _queuedBytes + packet.Length > MaxBytes * 2 || DelayLocked() > MaxDelay * 2)
            {
                return false;
            }

            EnqueueLocked(packet, keyframe: false);
            return true;
        }
    }

    /// <summary>Mensagem de controle (pedido de quadro-chave, pausa): sempre vai.</summary>
    public void Enqueue(byte[] packet)
    {
        lock (_lock)
        {
            if (!_completed)
            {
                EnqueueLocked(packet, keyframe: false);
            }
        }
    }

    /// <summary>O próximo vídeo só vai a partir de um quadro-chave (ex.: o encoder foi trocado).</summary>
    public void ResetToKeyframe()
    {
        lock (_lock)
        {
            _waitingForKeyframe = true;
        }
    }

    /// <summary>Termina a leitura depois dos pacotes que já estão na fila.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _available.Release();
    }

    /// <summary>
    /// Os pacotes em ordem, para um único leitor. O pacote conta como em envio até o leitor pedir o próximo,
    /// então envie-o dentro do laço.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            Entry? entry;
            lock (_lock)
            {
                if (!_entries.TryDequeue(out entry))
                {
                    yield break;
                }

                _sending = entry;
            }

            yield return entry.Data;

            lock (_lock)
            {
                _sending = null;
                _queuedBytes -= entry.Data.Length;
                if (entry.IsKeyframe)
                {
                    _keyframeSentAt = _time.GetTimestamp();
                }
            }
        }
    }

    private void EnqueueLocked(byte[] packet, bool keyframe)
    {
        _entries.Enqueue(new Entry(packet, keyframe, _time.GetTimestamp()));
        _queuedBytes += packet.Length;
        _available.Release();
    }

    private TimeSpan DelayLocked()
    {
        var oldest = _sending ?? (_entries.Count > 0 ? _entries.Peek() : null);
        if (oldest is null || oldest.IsKeyframe)
        {
            return TimeSpan.Zero;
        }

        return _time.GetElapsedTime(Math.Max(oldest.EnqueuedAt, _keyframeSentAt));
    }

    private sealed record Entry(byte[] Data, bool IsKeyframe, long EnqueuedAt);
}
