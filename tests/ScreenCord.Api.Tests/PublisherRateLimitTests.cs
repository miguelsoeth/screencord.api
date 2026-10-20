using ScreenCord.Api.Realtime;

namespace ScreenCord.Api.Tests;

/// <summary>Limite da taxa que quem compartilha manda pelo relay.</summary>
public sealed class PublisherRateLimitTests
{
    private const int Kbps = 1500;

    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>Manda <paramref name="bytesPerSecond"/> em pacotes de 10 KB durante <paramref name="seconds"/> s.</summary>
    private static List<RateDecision> Send(PublisherRateLimit limit, FakeTime time, double bytesPerSecond, int seconds)
    {
        const int packet = 10_000;
        var interval = TimeSpan.FromSeconds(packet / bytesPerSecond);
        var decisions = new List<RateDecision>();
        for (var sent = 0.0; sent < bytesPerSecond * seconds; sent += packet)
        {
            time.Advance(interval);
            decisions.Add(limit.Take(packet));
        }

        return decisions;
    }

    [Fact]
    public void NaTaxaPermitida_RepassaTudo()
    {
        var time = new FakeTime();
        var limit = new PublisherRateLimit(Kbps, time);

        var decisions = Send(limit, time, limit.BytesPerSecond, seconds: 120);

        Assert.All(decisions, d => Assert.Equal(RateDecision.Forward, d));
    }

    [Fact]
    public void RajadaDentroDaFolga_Passa()
    {
        var time = new FakeTime();
        var limit = new PublisherRateLimit(Kbps, time);

        // Quadro-chave grande logo no começo.
        Assert.Equal(RateDecision.Forward, limit.Take(PublisherRateLimit.MinBurstBytes));
    }

    [Fact]
    public void PoucoAcimaDaTaxa_DescartaOExcessoSemEncerrar()
    {
        var time = new FakeTime();
        var limit = new PublisherRateLimit(Kbps, time);

        var decisions = Send(limit, time, limit.BytesPerSecond * 1.2, seconds: 120);

        Assert.Contains(RateDecision.Drop, decisions);
        Assert.DoesNotContain(RateDecision.Disconnect, decisions);
    }

    [Fact]
    public void MuitoAcimaDaTaxa_Encerra()
    {
        var time = new FakeTime();
        var limit = new PublisherRateLimit(Kbps, time);

        // Anuncia 1500 kbps e manda 10 vezes isso.
        var decisions = Send(limit, time, Kbps * 1000 / 8 * 10, seconds: 10);

        Assert.Contains(RateDecision.Disconnect, decisions);
    }

    [Fact]
    public void DepoisDeUmaRajadaDescartada_VoltaAoNormal()
    {
        var time = new FakeTime();
        var limit = new PublisherRateLimit(Kbps, time);
        Send(limit, time, limit.BytesPerSecond * 1.5, seconds: 5);

        time.Advance(TimeSpan.FromSeconds(PublisherRateLimit.WindowSeconds));
        var decisions = Send(limit, time, limit.BytesPerSecond, seconds: 60);

        Assert.All(decisions, d => Assert.Equal(RateDecision.Forward, d));
    }
}
