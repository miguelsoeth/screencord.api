using ScreenCord.Contracts;

namespace ScreenCord.Api.Realtime;

/// <summary>
/// Quem está conectado a cada grupo e quem está compartilhando a tela, em memória. Cada conexão do hub
/// fica em no máximo uma sala e tem no máximo um compartilhamento, sempre na sala em que está; um usuário
/// com o aplicativo aberto em dois lugares conta uma vez só. O estado vale para esta instância da API:
/// para escalar horizontalmente, troque por um armazenamento compartilhado (ex.: Redis).
/// </summary>
public sealed class GroupPresence
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (Guid UserId, Guid GroupId)> _connections = [];
    private readonly Dictionary<string, ScreenShareDto> _shares = [];
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
                _shares.Remove(connectionId);
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
            _shares.Remove(connectionId);
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
                _shares.Remove(connectionId);
            }

            if (removed.Count > 0)
            {
                _version++;
            }

            return removed.Count;
        }
    }

    /// <summary>
    /// Começa o compartilhamento de tela da conexão na sala em que ela está. Um compartilhamento anterior
    /// do mesmo usuário na mesma sala (ex.: outra janela) é substituído. Retorna a sala, ou null se a conexão
    /// não está em nenhuma.
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
                .Where(s => s.Key != connectionId && s.Value.UserId == current.UserId &&
                            _connections.TryGetValue(s.Key, out var c) && c.GroupId == current.GroupId)
                .Select(s => s.Key)
                .ToList();
            foreach (var other in replaced)
            {
                _shares.Remove(other);
            }

            var share = new ScreenShareDto(current.UserId, start.Width, start.Height, start.FrameRate, start.BitrateKbps, start.HasAudio, start.Kind);
            _shares[connectionId] = share;
            _version++;
            return (current.GroupId, share);
        }
    }

    /// <summary>Encerra o compartilhamento da conexão. Retorna a sala e quem compartilhava, ou null se não havia.</summary>
    public (Guid GroupId, Guid UserId)? StopShare(string connectionId)
    {
        lock (_lock)
        {
            return StopShareCore(connectionId);
        }
    }

    /// <summary>Encerra o compartilhamento da conexão se ele acontece numa sala diferente de <paramref name="groupId"/>.</summary>
    public (Guid GroupId, Guid UserId)? StopShareIfInOtherGroup(string connectionId, Guid groupId)
    {
        lock (_lock)
        {
            return _connections.TryGetValue(connectionId, out var current) && current.GroupId != groupId
                ? StopShareCore(connectionId)
                : null;
        }
    }

    /// <summary>Encerra o compartilhamento de um usuário numa sala, venha de que conexão vier (ex.: o canal de mídia caiu).</summary>
    public bool StopShare(Guid groupId, Guid userId)
    {
        lock (_lock)
        {
            var removed = _shares
                .Where(s => s.Value.UserId == userId && _connections.TryGetValue(s.Key, out var c) && c.GroupId == groupId)
                .Select(s => s.Key)
                .ToList();
            foreach (var connectionId in removed)
            {
                _shares.Remove(connectionId);
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
    /// quem estava transmitindo, para fechar o canal de mídia de cada um.
    /// </summary>
    public IReadOnlyList<Guid> StopSharesNotAllowed(Guid groupId, GroupSettings settings)
    {
        lock (_lock)
        {
            var removed = _shares
                .Where(s => !settings.Allows(s.Value.Kind) && _connections.TryGetValue(s.Key, out var c) && c.GroupId == groupId)
                .ToList();
            foreach (var share in removed)
            {
                _shares.Remove(share.Key);
            }

            if (removed.Count > 0)
            {
                _version++;
            }

            return removed.Select(s => s.Value.UserId).Distinct().ToList();
        }
    }

    public ScreenShareDto? FindShare(Guid groupId, Guid userId)
    {
        lock (_lock)
        {
            return SharesIn(groupId).FirstOrDefault(s => s.UserId == userId);
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

    private (Guid GroupId, Guid UserId)? StopShareCore(string connectionId)
    {
        if (!_shares.Remove(connectionId, out var share) || !_connections.TryGetValue(connectionId, out var current))
        {
            return null;
        }

        _version++;
        return (current.GroupId, share.UserId);
    }

    private List<Guid> ConnectedUsers(Guid groupId) =>
        _connections.Values.Where(c => c.GroupId == groupId).Select(c => c.UserId).Distinct().ToList();

    private List<ScreenShareDto> SharesIn(Guid groupId) =>
        _shares
            .Where(s => _connections.TryGetValue(s.Key, out var c) && c.GroupId == groupId)
            .Select(s => s.Value)
            .DistinctBy(s => s.UserId)
            .ToList();
}

/// <summary>Quem está numa sala num dado momento (só os ids); <see cref="GroupQueries"/> completa com os dados dos usuários.</summary>
public sealed record PresenceSnapshot(Guid GroupId, IReadOnlyList<Guid> UserIds, long Version, IReadOnlyList<ScreenShareDto> Shares);
