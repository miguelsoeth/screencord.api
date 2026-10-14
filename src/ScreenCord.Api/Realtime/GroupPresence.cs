namespace ScreenCord.Api.Realtime;

/// <summary>
/// Quem está conectado a cada grupo, em memória. Cada conexão do hub fica em no máximo uma sala; um
/// usuário com o aplicativo aberto em dois lugares conta uma vez só. O estado vale para esta instância
/// da API: para escalar horizontalmente, troque por um armazenamento compartilhado (ex.: Redis).
/// </summary>
public sealed class GroupPresence
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (Guid UserId, Guid GroupId)> _connections = [];
    private long _version;

    /// <summary>Coloca a conexão na sala. Retorna a sala anterior, se ela estava em outra.</summary>
    public Guid? Join(string connectionId, Guid userId, Guid groupId)
    {
        lock (_lock)
        {
            Guid? previous = _connections.TryGetValue(connectionId, out var current) ? current.GroupId : null;
            _connections[connectionId] = (userId, groupId);
            _version++;
            return previous == groupId ? null : previous;
        }
    }

    /// <summary>Tira a conexão da sala em que estiver. Retorna essa sala, ou null se não estava em nenhuma.</summary>
    public Guid? Leave(string connectionId)
    {
        lock (_lock)
        {
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
            }

            if (removed.Count > 0)
            {
                _version++;
            }

            return removed.Count;
        }
    }

    public PresenceSnapshot Snapshot(Guid groupId)
    {
        lock (_lock)
        {
            return new PresenceSnapshot(groupId, ConnectedUsers(groupId), _version);
        }
    }

    public IReadOnlyList<PresenceSnapshot> Snapshot(IEnumerable<Guid> groupIds)
    {
        lock (_lock)
        {
            return groupIds.Distinct().Select(id => new PresenceSnapshot(id, ConnectedUsers(id), _version)).ToList();
        }
    }

    private List<Guid> ConnectedUsers(Guid groupId) =>
        _connections.Values.Where(c => c.GroupId == groupId).Select(c => c.UserId).Distinct().ToList();
}

/// <summary>Quem está numa sala num dado momento (só os ids); <see cref="GroupQueries"/> completa com os dados dos usuários.</summary>
public sealed record PresenceSnapshot(Guid GroupId, IReadOnlyList<Guid> UserIds, long Version);
