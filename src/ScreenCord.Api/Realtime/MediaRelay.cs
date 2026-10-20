using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using ScreenCord.Contracts;

namespace ScreenCord.Api.Realtime;

/// <summary>
/// Repassa a mídia do compartilhamento de tela de quem compartilha para quem assiste, sem decodificar.
/// Cada espectador tem a própria fila: quem tem conexão lenta perde quadros (e volta no próximo quadro-chave)
/// sem atrasar os outros nem quem compartilha.
/// </summary>
public sealed class MediaRelay(TimeProvider time, ILogger<MediaRelay> logger)
{
    /// <summary>Intervalo mínimo entre pedidos de quadro-chave repassados a quem compartilha.</summary>
    private static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan PublisherWait = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(Guid GroupId, Guid SharerId), Session> _sessions = new();

    /// <summary>
    /// Recebe os pacotes de quem compartilha até a conexão fechar ou o compartilhamento acabar. Retorna
    /// verdadeiro se este ainda era o canal do compartilhamento ao terminar (não foi substituído nem encerrado).
    /// </summary>
    public async Task<bool> PublishAsync(WebSocket socket, Guid groupId, Guid sharerId, int bitrateKbps, CancellationToken cancellationToken)
    {
        var session = new Session(socket, bitrateKbps, time, logger);
        if (_sessions.TryRemove((groupId, sharerId), out var previous))
        {
            // Outra janela da mesma conta recomeçou o compartilhamento: quem assistia passa a receber desta.
            session.AdoptViewers(previous);
            previous.ClosePublisher();
        }

        _sessions[(groupId, sharerId)] = session;
        try
        {
            await session.ReceiveFromPublisherAsync(cancellationToken);
        }
        finally
        {
            if (_sessions.TryRemove(new KeyValuePair<(Guid, Guid), Session>((groupId, sharerId), session)))
            {
                session.CloseViewers();
            }
        }

        return !session.WasStopped && !_sessions.ContainsKey((groupId, sharerId));
    }

    /// <summary>Envia ao espectador os pacotes do compartilhamento até ele sair ou o compartilhamento acabar.</summary>
    /// <remarks>
    /// O compartilhamento é anunciado pelo hub um instante antes de quem compartilha abrir o canal de mídia;
    /// quem chega nesse intervalo espera o canal por até alguns segundos, enquanto <paramref name="isShareActive"/> valer.
    /// </remarks>
    public async Task WatchAsync(WebSocket socket, Guid groupId, Guid sharerId, Guid viewerId, Func<bool> isShareActive, CancellationToken cancellationToken)
    {
        Session? session;
        var deadline = Environment.TickCount64 + (long)PublisherWait.TotalMilliseconds;
        while (!_sessions.TryGetValue((groupId, sharerId), out session))
        {
            if (Environment.TickCount64 > deadline || !isShareActive())
            {
                await CloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "Compartilhamento encerrado.");
                return;
            }

            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        Viewer? viewer = null;
        viewer = new Viewer(socket, viewerId, session.QueueLimitBytes, () => FindSession(viewer!)?.RequestKeyframe());
        session.AddViewer(viewer);
        try
        {
            await viewer.RunAsync(cancellationToken);
        }
        finally
        {
            // Pode ter mudado de sessão (outra janela de quem compartilha assumiu): remove de onde estiver.
            foreach (var s in _sessions.Values)
            {
                s.RemoveViewer(viewer);
            }
        }
    }

    /// <summary>Encerra o compartilhamento: quem compartilha e quem assiste são desconectados.</summary>
    public void Stop(Guid groupId, Guid sharerId)
    {
        if (_sessions.TryRemove((groupId, sharerId), out var session))
        {
            session.ClosePublisher();
            session.CloseViewers();
        }
    }

    /// <summary>Desconecta o usuário de tudo o que ele assiste na sala (ex.: ele saiu da sala ou do grupo).</summary>
    public void DisconnectViewer(Guid groupId, Guid viewerId)
    {
        foreach (var ((group, _), session) in _sessions)
        {
            if (group == groupId)
            {
                session.CloseViewer(viewerId);
            }
        }
    }

    private Session? FindSession(Viewer viewer) => _sessions.Values.FirstOrDefault(s => s.Contains(viewer));

    public int ViewerCount(Guid groupId, Guid sharerId) =>
        _sessions.TryGetValue((groupId, sharerId), out var session) ? session.ViewerCount : 0;

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(status, description, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private sealed class Session(WebSocket publisher, int bitrateKbps, TimeProvider time, ILogger logger)
    {
        private readonly Lock _lock = new();
        private readonly List<Viewer> _viewers = [];
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        private readonly PublisherRateLimit _rateLimit = new(bitrateKbps, time);
        private long _lastKeyframeRequestTicks;

        /// <summary>Cerca de 1 s de vídeo (no mínimo 512 KB, para caber um quadro-chave): mais que isso, o espectador está atrasado.</summary>
        public int QueueLimitBytes { get; } = Math.Max(512 * 1024, bitrateKbps * 1000 / 8);

        public int ViewerCount
        {
            get
            {
                lock (_lock)
                {
                    return _viewers.Count;
                }
            }
        }

        public void AddViewer(Viewer viewer)
        {
            lock (_lock)
            {
                _viewers.Add(viewer);
            }

            RequestKeyframe();
        }

        public bool Contains(Viewer viewer)
        {
            lock (_lock)
            {
                return _viewers.Contains(viewer);
            }
        }

        public void RemoveViewer(Viewer viewer)
        {
            lock (_lock)
            {
                _viewers.Remove(viewer);
            }
        }

        public void AdoptViewers(Session previous)
        {
            List<Viewer> viewers;
            lock (previous._lock)
            {
                viewers = [.. previous._viewers];
                previous._viewers.Clear();
            }

            lock (_lock)
            {
                foreach (var viewer in viewers)
                {
                    viewer.ResetToKeyframe();
                    _viewers.Add(viewer);
                }
            }
        }

        public void CloseViewer(Guid viewerId)
        {
            List<Viewer> closing;
            lock (_lock)
            {
                closing = _viewers.Where(v => v.UserId == viewerId).ToList();
                _viewers.RemoveAll(v => v.UserId == viewerId);
            }

            foreach (var viewer in closing)
            {
                viewer.Close();
            }
        }

        public void CloseViewers()
        {
            List<Viewer> closing;
            lock (_lock)
            {
                closing = [.. _viewers];
                _viewers.Clear();
            }

            foreach (var viewer in closing)
            {
                viewer.Close();
            }
        }

        /// <summary>Encerrado de fora (pelo hub ou por outro canal que assumiu), e não pela queda da conexão.</summary>
        public bool WasStopped => _closed.IsCancellationRequested;

        public void ClosePublisher() => _closed.Cancel();

        public async Task ReceiveFromPublisherAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                while (true)
                {
                    var length = 0;
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        if (length == buffer.Length)
                        {
                            if (buffer.Length >= MediaContract.MaxPacketSize)
                            {
                                await CloseAsync(publisher, WebSocketCloseStatus.MessageTooBig, "Pacote grande demais.");
                                return;
                            }

                            var larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MediaContract.MaxPacketSize));
                            buffer.AsSpan(0, length).CopyTo(larger);
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = larger;
                        }

                        result = await publisher.ReceiveAsync(buffer.AsMemory(length), linked.Token);
                        length += result.Count;
                    }
                    while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseAsync(publisher, WebSocketCloseStatus.NormalClosure, "");
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Binary ||
                        !MediaContract.TryReadHeader(buffer.AsSpan(0, length), out var type, out var flags, out _) ||
                        type is not (MediaContract.Video or MediaContract.Audio))
                    {
                        continue;
                    }

                    switch (_rateLimit.Take(length))
                    {
                        case RateDecision.Drop:
                            if (type == MediaContract.Video)
                            {
                                // Sem este pacote o vídeo não decodifica: quem assiste recomeça do próximo quadro-chave.
                                ResetViewersToKeyframe();
                            }

                            continue;
                        case RateDecision.Disconnect:
                            logger.LogWarning(
                                "Canal de mídia encerrado: enviou acima da taxa anunciada ({BitrateKbps} kbps) por tempo demais.", bitrateKbps);
                            await CloseAsync(publisher, WebSocketCloseStatus.PolicyViolation, "Taxa acima da anunciada.");
                            return;
                    }

                    Broadcast(new Packet(buffer.AsSpan(0, length).ToArray(), type, (flags & MediaContract.KeyframeFlag) != 0));
                }
            }
            catch (OperationCanceledException)
            {
                if (_closed.IsCancellationRequested)
                {
                    await CloseAsync(publisher, WebSocketCloseStatus.NormalClosure, "Compartilhamento encerrado.");
                }
            }
            catch (WebSocketException ex)
            {
                logger.LogDebug("Canal de mídia de quem compartilha caiu ({Error}).", ex.WebSocketErrorCode);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void ResetViewersToKeyframe()
        {
            lock (_lock)
            {
                foreach (var viewer in _viewers)
                {
                    viewer.ResetToKeyframe();
                }
            }
        }

        private void Broadcast(Packet packet)
        {
            Viewer[] viewers;
            lock (_lock)
            {
                viewers = [.. _viewers];
            }

            var needsKeyframe = false;
            foreach (var viewer in viewers)
            {
                needsKeyframe |= !viewer.Offer(packet);
            }

            if (needsKeyframe)
            {
                RequestKeyframe();
            }
        }

        public void RequestKeyframe()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastKeyframeRequestTicks);
            if (now - last < KeyframeRequestInterval.TotalMilliseconds ||
                Interlocked.CompareExchange(ref _lastKeyframeRequestTicks, now, last) != last)
            {
                return;
            }

            _ = SendKeyframeRequestAsync();
        }

        private async Task SendKeyframeRequestAsync()
        {
            var message = new byte[MediaContract.HeaderSize];
            MediaContract.WriteHeader(message, MediaContract.KeyframeRequest, 0, 0);
            if (!await _sendGate.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await publisher.SendAsync(message, WebSocketMessageType.Binary, true, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                _sendGate.Release();
            }
        }
    }

    private sealed record Packet(byte[] Data, byte Type, bool IsKeyframe);

    private sealed class Viewer(WebSocket socket, Guid userId, int queueLimitBytes, Action requestKeyframe)
    {
        private readonly Channel<Packet> _queue = Channel.CreateUnbounded<Packet>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _closed = new();
        private long _queuedBytes;
        private volatile bool _waitingForKeyframe = true;
        private volatile bool _videoPaused;

        public Guid UserId { get; } = userId;

        /// <summary>Enfileira o pacote. Retorna falso se o espectador precisa de um quadro-chave para continuar.</summary>
        public bool Offer(Packet packet)
        {
            if (packet.Type == MediaContract.Video)
            {
                if (_videoPaused)
                {
                    // Não está olhando: economiza a banda do vídeo; o áudio continua.
                    return true;
                }

                if (_waitingForKeyframe && !packet.IsKeyframe)
                {
                    return false;
                }

                if (Interlocked.Read(ref _queuedBytes) + packet.Data.Length > queueLimitBytes)
                {
                    // Atrasado demais: descarta até o próximo quadro-chave em vez de acumular atraso.
                    _waitingForKeyframe = true;
                    return false;
                }

                _waitingForKeyframe = false;
            }
            else if (Interlocked.Read(ref _queuedBytes) + packet.Data.Length > queueLimitBytes * 2)
            {
                return true;
            }

            Interlocked.Add(ref _queuedBytes, packet.Data.Length);
            _queue.Writer.TryWrite(packet);
            return true;
        }

        public void ResetToKeyframe() => _waitingForKeyframe = true;

        public void Close() => _closed.Cancel();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            var receive = DrainIncomingAsync(linked);
            try
            {
                await foreach (var packet in _queue.Reader.ReadAllAsync(linked.Token))
                {
                    Interlocked.Add(ref _queuedBytes, -packet.Data.Length);
                    await socket.SendAsync(packet.Data, WebSocketMessageType.Binary, true, linked.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }

            linked.Cancel();
            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Compartilhamento encerrado.");
            await receive;
        }

        /// <summary>
        /// O espectador não envia mídia, só pedidos de quadro-chave e de pausa do vídeo; a leitura também detecta o fechamento.
        /// </summary>
        private async Task DrainIncomingAsync(CancellationTokenSource linked)
        {
            var buffer = new byte[1024];
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, linked.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (!result.EndOfMessage || result.Count != MediaContract.HeaderSize)
                    {
                        continue;
                    }

                    switch (buffer[0])
                    {
                        case MediaContract.PauseVideo:
                            _videoPaused = true;
                            break;
                        case MediaContract.ResumeVideo when _videoPaused:
                            _waitingForKeyframe = true;
                            _videoPaused = false;
                            requestKeyframe();
                            break;
                        case MediaContract.KeyframeRequest when !_videoPaused:
                            // Recomeça do próximo quadro-chave; quem compartilha recebe no máximo um pedido a cada meio segundo.
                            _waitingForKeyframe = true;
                            requestKeyframe();
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }

            try
            {
                linked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
