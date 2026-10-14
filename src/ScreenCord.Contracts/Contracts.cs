using System.Text.Json.Serialization;

namespace ScreenCord.Contracts;

/// <summary>Corpo de <c>POST /auth/discord</c>: o access token OAuth2 emitido pelo Discord para este aplicativo.</summary>
public sealed record DiscordLoginRequest(string AccessToken);

/// <summary>Sessão da API central. <see cref="Token"/> é enviado como <c>Authorization: Bearer</c>.</summary>
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAtUtc, UserDto User);

/// <summary>Relação entre o usuário autenticado e o usuário retornado.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelationshipStatus>))]
public enum RelationshipStatus
{
    None,
    Friend,
    /// <summary>O usuário autenticado enviou a solicitação e aguarda resposta.</summary>
    PendingOutgoing,
    /// <summary>O usuário autenticado recebeu a solicitação e pode aceitá-la.</summary>
    PendingIncoming,
}

public sealed record UserDto(
    Guid Id,
    string DiscordId,
    string Username,
    string? GlobalName,
    string? Discriminator,
    string? AvatarHash,
    RelationshipStatus Relationship);

public sealed record FriendRequestCreate(Guid UserId);

public sealed record FriendRequestsResponse(IReadOnlyList<UserDto> Incoming, IReadOnlyList<UserDto> Outgoing);

/// <summary>Corpo de erro da API. <see cref="Message"/> é um texto em português próprio para exibição.</summary>
public sealed record ErrorResponse(string Code, string Message);

/// <summary>Usuário conectado a uma sala, com o necessário para exibir nome e foto.</summary>
public sealed record PresenceUserDto(
    Guid Id,
    string DiscordId,
    string Username,
    string? GlobalName,
    string? Discriminator,
    string? AvatarHash);

/// <summary>
/// Grupo do qual o usuário autenticado faz parte. <see cref="ConnectedUsers"/> lista quem está
/// conectado à sala agora; <see cref="PresenceVersion"/> cresce a cada mudança de presença e permite
/// descartar notificações que chegarem fora de ordem.
/// </summary>
public sealed record GroupDto(
    Guid Id,
    string Name,
    int MemberCount,
    IReadOnlyList<PresenceUserDto> ConnectedUsers,
    long PresenceVersion)
{
    [JsonIgnore]
    public IReadOnlyList<Guid> ConnectedUserIds => ConnectedUsers?.Select(u => u.Id).ToList() ?? [];
}

public sealed record GroupMemberDto(
    Guid Id,
    string DiscordId,
    string Username,
    string? GlobalName,
    string? Discriminator,
    string? AvatarHash,
    bool IsConnected);

public sealed record GroupDetailsDto(Guid Id, string Name, IReadOnlyList<GroupMemberDto> Members, long PresenceVersion);

public sealed record GroupCreate(string Name);

public sealed record GroupMemberCreate(Guid UserId);

/// <summary>Quem está conectado a um grupo, enviado pelo hub a todos os integrantes a cada entrada ou saída.</summary>
public sealed record GroupPresenceDto(Guid GroupId, IReadOnlyList<PresenceUserDto> ConnectedUsers, long Version)
{
    [JsonIgnore]
    public IReadOnlyList<Guid> ConnectedUserIds => ConnectedUsers?.Select(u => u.Id).ToList() ?? [];
}

/// <summary>Nomes do hub de grupos em tempo real (SignalR), compartilhados entre a API e o aplicativo.</summary>
public static class GroupsHubContract
{
    public const string Path = "/hubs/groups";

    public const int MaxNameLength = 40;

    public const int MaxMembers = 50;

    /// <summary>Cliente → servidor: entra na sala do grupo (sai da anterior). Retorna a presença atual ou null se não for integrante.</summary>
    public const string JoinGroup = "JoinGroup";

    /// <summary>Cliente → servidor: sai da sala atual.</summary>
    public const string LeaveGroup = "LeaveGroup";

    /// <summary>Servidor → cliente: <see cref="GroupPresenceDto"/>.</summary>
    public const string PresenceChanged = "PresenceChanged";

    /// <summary>Servidor → cliente: <see cref="GroupDto"/> criado ou com integrantes alterados.</summary>
    public const string GroupChanged = "GroupChanged";

    /// <summary>Servidor → cliente: id do grupo do qual o usuário deixou de fazer parte.</summary>
    public const string GroupRemoved = "GroupRemoved";
}
