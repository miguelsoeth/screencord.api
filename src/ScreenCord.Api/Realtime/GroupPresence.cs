using ScreenCord.Contracts;

namespace ScreenCord.Api.Realtime;

/// <summary>
/// Quem está conectado a cada grupo e quem está transmitindo, em memória. Cada conexão do hub fica em no máximo
/// uma sala e tem no máximo uma transmissão de cada tipo (tela e câmera), sempre na sala em que está; um usuário
/// com o aplicativo aberto em dois lugares conta uma vez só. O estado vale para esta instância da API:
/// para escalar horizontalmente, troque por um armazenamento compartilhado (ex.: Redis).
/// </summary>
public sealed class GroupPresence
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (Guid UserId, Guid GroupId)> _connections = [];
    private readonly Dictionary<(string ConnectionId, ShareKind Kind), ScreenShareDto> _shares = [];
    private long _version;

    /// <summary>Coloca a conexão na sala. Retorna a sala anterior, se ela estava em outra.</summary>
    /// <remarks>Trocar de sala encerra o compartilhamento de tela da conexão.</remarks>
    public Guid? Join(string connectionId, Guid userId, Guid groupId)
    {
        lock (_lock)
        {
            Guid? previous = _connections.TryGetValue(connectionId, out var current) ? current.GroupId : null;
            _connections[connectionId] = (userId, groupId);
            if (previous != groupId)
            {
                RemoveSharesOf(connectionId);
            }

            _version++;
            return previous == groupId ? null : previous;
        }
    }

    /// <summary>Tira a conexão da sala em que estiver. Retorna essa sala, ou null se não estava em nenhuma.</summary>
    public Guid? Leave(string connectionId)
    {
        lock (_lock)
        {
            RemoveSharesOf(connectionId);
            if (!_connections.Remove(connectionId, out var current))
            {
                return null;
            }

            _version++;
            return current.GroupId;
        }
    }

    /// <summary>Tira todas as conexões de um usuário da sala (ex.: ele saiu do grupo). Retorna quantas eram.</summary>
    public int RemoveUser(Guid userId, Guid groupId)
    {
        lock (_lock)
        {
            var removed = _connections
                .Where(c => c.Value.UserId == userId && c.Value.GroupId == groupId)
                .Select(c => c.Key)
                .ToList();
            foreach (var connectionId in removed)
            {
                _connections.Remove(connectionId);
                RemoveSharesOf(connectionId);
            }

            if (removed.Count > 0)
            {
                _version++;
            }

            return removed.Count;
        }
    }

    /// <summary>
    /// Começa a transmissão da conexão na sala em que ela está. Uma transmissão anterior do mesmo usuário, do mesmo tipo,
    /// na mesma sala (ex.: outra janela) é substituída; a do outro tipo continua (tela e câmera podem estar no ar juntas).
    /// Retorna a sala, ou null se a conexão não está em nenhuma.
    /// </summary>
    public (Guid GroupId, ScreenShareDto Share)? StartShare(string connectionId, ScreenShareStart start)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(connectionId, out var current))
            {
                return null;
            }

            var replaced = _shares
                .Where(s => s.Key.ConnectionId != connectionId && s.Key.Kind == start.Kind && s.Value.UserId == current.UserId &&
                            _connections.TryGetValue(s.Key.ConnectionId, out var c) && c.GroupId == current.GroupId)
                .Select(s => s.Key)
                .ToList();
            foreach (var other in replaced)
            {
                _shares.Remove(other);
            }

            var share = new ScreenShareDto(current.UserId, start.Width, start.Height, start.FrameRate, start.BitrateKbps, start.HasAudio, start.Kind);
            _shares[(connectionId, start.Kind)] = share;
            _version++;
            return (current.GroupId, share);
        }
    }

    /// <summary>Encerra todas as transmissões da conexão (tela e câmera). Retorna as que havia, cada uma com a sala.</summary>
    public IReadOnlyList<StoppedShare> StopShares(string connectionId)
    {
        lock (_lock)
        {
            return StopSharesCore(connectionId, null);
        }
    }

    /// <summary>Encerra a transmissão da conexão desse tipo. Retorna a sala e quem transmitia, ou null se não havia.</summary>
    public StoppedShare? StopShare(string connectionId, ShareKind kind)
    {
        lock (_lock)
        {
            return StopSharesCore(connectionId, kind).FirstOrDefault();
        }
    }

    /// <summary>Encerra as transmissões da conexão se elas acontecem numa sala diferente de <paramref name="groupId"/>.</summary>
    public IReadOnlyList<StoppedShare> StopSharesIfInOtherGroup(string connectionId, Guid groupId)
    {
        lock (_lock)
        {
            return _connections.TryGetValue(connectionId, out var current) && current.GroupId != groupId
                ? StopSharesCore(connectionId, null)
                : [];
        }
    }

    /// <summary>Encerra a transmissão de um usuário numa sala, venha de que conexão vier (ex.: o canal de mídia caiu).</summary>
    public bool StopShare(Guid groupId, Guid userId, ShareKind kind)
    {
        lock (_lock)
        {
            var removed = _shares
                .Where(s => s.Key.Kind == kind && s.Value.UserId == userId &&
                            _connections.TryGetValue(s.Key.ConnectionId, out var c) && c.GroupId == groupId)
                .Select(s => s.Key)
                .ToList();
            foreach (var key in removed)
            {
                _shares.Remove(key);
            }

            if (removed.Count > 0)
            {
                _version++;
            }

            return removed.Count > 0;
        }
    }

    /// <summary>Sala em que a conexão está, ou null.</summary>
    public Guid? GroupOf(string connectionId)
    {
        lock (_lock)
        {
            return _connections.TryGetValue(connectionId, out var current) ? current.GroupId : null;
        }
    }

    /// <summary>
    /// Encerra as transmissões de uma sala que o grupo deixou de aceitar (ex.: o dono desligou a câmera). Retorna
    /// quem estava transmitindo e o tipo, para fechar o canal de mídia de cada uma.
    /// </summary>
    public IReadOnlyList<StoppedShare> StopSharesNotAllowed(Guid groupId, GroupSettings settings)
    {
        lock (_lock)
        {
            var removed = _shares
                .Where(s => !settings.Allows(s.Key.Kind) && _connections.TryGetValue(s.Key.ConnectionId, out var c) && c.GroupId == groupId)
                .ToList();
            foreach (var share in removed)
            {
                _shares.Remove(share.Key);
            }

            if (removed.Count > 0)
            {
                _version++;
            }

            return removed.Select(s => new StoppedShare(groupId, s.Value.UserId, s.Key.Kind)).Distinct().ToList();
        }
    }

    /// <summary>A transmissão do usuário desse tipo na sala, ou null.</summary>
    public ScreenShareDto? FindShare(Guid groupId, Guid userId, ShareKind kind)
    {
        lock (_lock)
        {
            return SharesIn(groupId).FirstOrDefault(s => s.UserId == userId && s.Kind == kind);
        }
    }

    /// <summary>O usuário está na sala por alguma das conexões dele.</summary>
    public bool IsConnected(Guid userId, Guid groupId)
    {
        lock (_lock)
        {
            return _connections.Values.Any(c => c.UserId == userId && c.GroupId == groupId);
        }
    }

    public PresenceSnapshot Snapshot(Guid groupId)
    {
        lock (_lock)
        {
            return new PresenceSnapshot(groupId, ConnectedUsers(groupId), _version, SharesIn(groupId));
        }
    }

    public IReadOnlyList<PresenceSnapshot> Snapshot(IEnumerable<Guid> groupIds)
    {
        lock (_lock)
        {
            return groupIds.Distinct().Select(id => new PresenceSnapshot(id, ConnectedUsers(id), _version, SharesIn(id))).ToList();
        }
    }

    /// <summary>Remove as transmissões da conexão (só as do tipo <paramref name="kind"/>, se informado); a versão só muda se havia alguma.</summary>
    private List<StoppedShare> StopSharesCore(string connectionId, ShareKind? kind)
    {
        if (!_connections.TryGetValue(connectionId, out var current))
        {
            return [];
        }

        var stopped = new List<StoppedShare>();
        foreach (var key in _shares.Keys.Where(k => k.ConnectionId == connectionId && (kind is null || k.Kind == kind)).ToList())
        {
            stopped.Add(new StoppedShare(current.GroupId, _shares[key].UserId, key.Kind));
            _shares.Remove(key);
        }

        if (stopped.Count > 0)
        {
            _version++;
        }

        return stopped;
    }

    private void RemoveSharesOf(string connectionId)
    {
        foreach (var key in _shares.Keys.Where(k => k.ConnectionId == connectionId).ToList())
        {
            _shares.Remove(key);
        }
    }

    private List<Guid> ConnectedUsers(Guid groupId) =>
        _connections.Values.Where(c => c.GroupId == groupId).Select(c => c.UserId).Distinct().ToList();

    /// <summary>Uma por pessoa e tipo, a tela antes da câmera: quem só conhece uma transmissão por pessoa fica com a tela.</summary>
    private List<ScreenShareDto> SharesIn(Guid groupId) =>
        _shares
            .Where(s => _connections.TryGetValue(s.Key.ConnectionId, out var c) && c.GroupId == groupId)
            .Select(s => s.Value)
            .DistinctBy(s => (s.UserId, s.Kind))
            .OrderBy(s => s.Kind)
            .ToList();
}

/// <summary>Uma transmissão que acabou: a sala, quem transmitia e o tipo (para fechar o canal de mídia certo).</summary>
public sealed record StoppedShare(Guid GroupId, Guid UserId, ShareKind Kind);

/// <summary>Quem está numa sala num dado momento (só os ids); <see cref="GroupQueries"/> completa com os dados dos usuários.</summary>
public sealed record PresenceSnapshot(Guid GroupId, IReadOnlyList<Guid> UserIds, long Version, IReadOnlyList<ScreenShareDto> Shares);
