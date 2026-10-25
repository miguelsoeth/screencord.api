using ScreenCord.Contracts;

namespace ScreenCord.Api.Tests;

/// <summary>A fila de saída de mídia limitada por tempo (existe a mesma cópia nos testes do app).</summary>
public class MediaQueueTests
{
    private static readonly TimeSpan Ms100 = TimeSpan.FromMilliseconds(100);

    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private readonly FakeTime _time = new();

    private MediaQueue Queue(bool waitForKeyframe = false) => new(4000, _time, waitForKeyframe: waitForKeyframe);

    private static byte[] Packet(int size = 1000) => new byte[size];

    /// <summary>Lê os pacotes enfileirados (o último fica "em envio" até o próximo <c>MoveNextAsync</c>).</summary>
    private static async Task<int> ReadAsync(IAsyncEnumerator<byte[]> reader, int count)
    {
        var read = 0;
        for (var i = 0; i < count; i++)
        {
            Assert.True(await reader.MoveNextAsync());
            read++;
        }

        return read;
    }

    [Fact]
    public async Task RedeAcompanhando_EnviaTudoEmOrdem()
    {
        var queue = Queue();
        var packets = new[] { Packet(1), Packet(2), Packet(3) };
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(packets[0], keyframe: true));
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(packets[1], keyframe: false));
        Assert.True(queue.OfferAudio(packets[2]));
        queue.Complete();

        var received = new List<byte[]>();
        await foreach (var packet in queue.ReadAllAsync())
        {
            received.Add(packet);
        }

        Assert.Equal(packets, received);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Overflows);
    }

    [Fact]
    public async Task FilaAtrasada_DescartaAteAFilaEsvaziarEDepoisPedeQuadroChave()
    {
        var queue = Queue();
        var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        queue.OfferVideo(Packet(), keyframe: false);
        _time.Advance(Ms100 * 5);

        // O pacote mais antigo espera há 500 ms: a rede não acompanha.
        Assert.Equal(MediaOffer.Dropped, queue.OfferVideo(Packet(), keyframe: false));
        Assert.Equal(1, queue.Overflows);

        // Enquanto a fila não esvazia, nem quadro-chave entra (chegaria atrasado também).
        Assert.Equal(MediaOffer.Dropped, queue.OfferVideo(Packet(), keyframe: true));

        // Sai o pacote atrasado; o leitor fica esperando o próximo.
        await ReadAsync(reader, 1);
        var next = reader.MoveNextAsync();
        Assert.False(next.IsCompleted);
        Assert.Equal(TimeSpan.Zero, queue.Delay);

        Assert.Equal(MediaOffer.NeedsKeyframe, queue.OfferVideo(Packet(), keyframe: false));
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet(), keyframe: true));
        Assert.True(await next);
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet(), keyframe: false));
        Assert.Equal(1, queue.Overflows);
    }

    [Fact]
    public async Task AtrasoAbaixoDoLimite_NaoDescarta()
    {
        var queue = Queue();
        var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet(), keyframe: i == 0));
            _time.Advance(Ms100 * 3);
            await ReadAsync(reader, 1);
        }

        Assert.Equal(0, queue.Overflows);
    }

    [Fact]
    public async Task EnvioDeQuadroChaveDemorado_NaoContaComoAtraso()
    {
        var queue = Queue();
        var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        queue.OfferVideo(Packet(300_000), keyframe: true);
        await ReadAsync(reader, 1); // o quadro-chave está sendo enviado

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet(), keyframe: false));
        Assert.Equal(TimeSpan.Zero, queue.Delay);

        // Terminou: quem estava atrás dele conta a partir daqui.
        await ReadAsync(reader, 1);
        _time.Advance(Ms100 * 3);
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet(), keyframe: false));
        _time.Advance(Ms100 * 2);
        Assert.Equal(MediaOffer.Dropped, queue.OfferVideo(Packet(), keyframe: false));
    }

    [Fact]
    public void FilaCheiaDeBytes_DescartaMesmoSemAtraso()
    {
        var queue = Queue();
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet((int)queue.MaxBytes - 10), keyframe: true));
        Assert.Equal(MediaOffer.Dropped, queue.OfferVideo(Packet(100), keyframe: false));
        Assert.Equal(1, queue.Overflows);
    }

    [Fact]
    public void Audio_VaiAteODobroDoAtraso()
    {
        var queue = Queue();
        queue.OfferVideo(Packet(), keyframe: false);
        _time.Advance(Ms100 * 5);
        Assert.Equal(MediaOffer.Dropped, queue.OfferVideo(Packet(), keyframe: false));
        Assert.True(queue.OfferAudio(Packet(10)));

        _time.Advance(Ms100 * 4);
        Assert.False(queue.OfferAudio(Packet(10)));
    }

    [Fact]
    public void EspectadorNovo_PedeQuadroChaveLogo()
    {
        var queue = Queue(waitForKeyframe: true);
        Assert.Equal(MediaOffer.NeedsKeyframe, queue.OfferVideo(Packet(), keyframe: false));
        Assert.True(queue.OfferAudio(Packet()));
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet(), keyframe: true));
    }

    [Fact]
    public void ResetToKeyframe_EsperaOProximoQuadroChave()
    {
        var queue = Queue();
        queue.OfferVideo(Packet(), keyframe: true);
        queue.ResetToKeyframe();
        Assert.Equal(MediaOffer.NeedsKeyframe, queue.OfferVideo(Packet(), keyframe: false));
        Assert.Equal(MediaOffer.Queued, queue.OfferVideo(Packet(), keyframe: true));
        Assert.Equal(0, queue.Overflows);
    }

    [Fact]
    public async Task Complete_EntregaOQueFaltaEDepoisTermina()
    {
        var queue = Queue();
        queue.Enqueue(Packet(1));
        queue.Complete();
        queue.Enqueue(Packet(2));
        Assert.Equal(MediaOffer.Dropped, queue.OfferVideo(Packet(3), keyframe: true));

        var received = new List<byte[]>();
        await foreach (var packet in queue.ReadAllAsync())
        {
            received.Add(packet);
        }

        Assert.Single(received);
    }
}
