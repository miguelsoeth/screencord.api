namespace ScreenCord.Api.Realtime;

public enum RateDecision
{
    /// <summary>Dentro da taxa: repassa a quem assiste.</summary>
    Forward,

    /// <summary>Acima da taxa: descarta o pacote.</summary>
    Drop,

    /// <summary>Acima da taxa por tempo demais: encerra o canal de quem compartilha.</summary>
    Disconnect,
}

/// <summary>
/// Limita o que quem compartilha manda pelo relay à taxa anunciada no início do compartilhamento. Sem isto, a taxa
/// anunciada só valia no papel: um cliente modificado podia anunciar 1500 kbps e mandar muito mais, e a banda de
/// saída do servidor é essa taxa vezes o número de espectadores.
/// </summary>
/// <remarks>
/// Balde de fichas com folga para o que um cliente honesto faz (o codificador passa da taxa em quadros-chave e há o
/// áudio): o dobro da taxa anunciada mais <see cref="ExtraKbps"/>, com rajada de <see cref="BurstSeconds"/> s. O que
/// passa disso é descartado; se o descarte numa janela de <see cref="WindowSeconds"/> s passar de
/// <see cref="DroppedSecondsToDisconnect"/> s da taxa permitida, o canal é encerrado. Não é seguro entre threads:
/// cada canal de quem compartilha tem o seu, usado só pelo laço que recebe os pacotes.
/// </remarks>
public sealed class PublisherRateLimit
{
    public const int HeadroomFactor = 2;
    public const int ExtraKbps = 512;
    public const int BurstSeconds = 3;
    public const int WindowSeconds = 10;
    public const int DroppedSecondsToDisconnect = 3;

    /// <summary>A rajada sempre comporta um quadro-chave grande, mesmo em taxas baixas.</summary>
    public const int MinBurstBytes = 1024 * 1024;

    private readonly TimeProvider _time;
    private readonly double _capacity;
    private readonly double _maxDroppedPerWindow;
    private double _tokens;
    private long _lastRefill;
    private long _windowStart;
    private long _droppedInWindow;

    public PublisherRateLimit(int bitrateKbps, TimeProvider time)
    {
        _time = time;
        BytesPerSecond = (bitrateKbps * HeadroomFactor + ExtraKbps) * 1000.0 / 8;
        _capacity = Math.Max(BytesPerSecond * BurstSeconds, MinBurstBytes);
        _maxDroppedPerWindow = BytesPerSecond * DroppedSecondsToDisconnect;
        _tokens = _capacity;
        _lastRefill = _windowStart = time.GetTimestamp();
    }

    /// <summary>Taxa sustentada permitida, em bytes por segundo.</summary>
    public double BytesPerSecond { get; }

    public RateDecision Take(int bytes)
    {
        var now = _time.GetTimestamp();
        _tokens = Math.Min(_capacity, _tokens + _time.GetElapsedTime(_lastRefill, now).TotalSeconds * BytesPerSecond);
        _lastRefill = now;

        if (_tokens >= bytes)
        {
            _tokens -= bytes;
            return RateDecision.Forward;
        }

        if (_time.GetElapsedTime(_windowStart, now).TotalSeconds >= WindowSeconds)
        {
            _windowStart = now;
            _droppedInWindow = 0;
        }

        _droppedInWindow += bytes;
        return _droppedInWindow > _maxDroppedPerWindow ? RateDecision.Disconnect : RateDecision.Drop;
    }
}
