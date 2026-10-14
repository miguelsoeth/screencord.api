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
    long PresenceVersion,
    IReadOnlyList<ScreenShareDto>? ScreenShares = null)
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

/// <summary>
/// Quem está conectado a um grupo e quem está compartilhando a tela, enviado pelo hub a todos os
/// integrantes a cada entrada, saída, início ou fim de compartilhamento.
/// </summary>
public sealed record GroupPresenceDto(
    Guid GroupId,
    IReadOnlyList<PresenceUserDto> ConnectedUsers,
    long Version,
    IReadOnlyList<ScreenShareDto>? ScreenShares = null)
{
    [JsonIgnore]
    public IReadOnlyList<Guid> ConnectedUserIds => ConnectedUsers?.Select(u => u.Id).ToList() ?? [];
}

/// <summary>Pedido para começar a compartilhar a tela na sala em que a conexão está.</summary>
public sealed record ScreenShareStart(int Width, int Height, int FrameRate, int BitrateKbps, bool HasAudio);

/// <summary>Compartilhamento de tela ativo numa sala. A mídia passa por <see cref="MediaContract.Path"/>.</summary>
public sealed record ScreenShareDto(Guid UserId, int Width, int Height, int FrameRate, int BitrateKbps, bool HasAudio);

/// <summary>
/// Limites do compartilhamento de tela, validados pela API e oferecidos pelo aplicativo.
/// A largura acompanha a proporção da tela ou janela; a altura é a resolução escolhida (540p, 720p, 1080p).
/// </summary>
public static class ScreenShareLimits
{
    public static readonly int[] Heights = [540, 720, 1080];

    public static readonly int[] FrameRates = [30, 60];

    public static readonly int[] BitratesKbps = [2000, 4000, 5000];

    /// <summary>Janelas muito largas (ex.: monitores ultrawide) podem passar de 16:9.</summary>
    public const int MaxWidth = 3840;

    public const int MinSize = 64;

    public static bool IsValid(ScreenShareStart? start) =>
        start is not null &&
        start.Width is >= MinSize and <= MaxWidth && start.Width % 2 == 0 &&
        start.Height >= MinSize && start.Height <= Heights.Max() && start.Height % 2 == 0 &&
        FrameRates.Contains(start.FrameRate) &&
        BitratesKbps.Contains(start.BitrateKbps);
}

/// <summary>
/// Canal de mídia do compartilhamento de tela: um WebSocket por compartilhamento, autenticado pelo
/// cabeçalho <c>Authorization</c> como o hub. Quem compartilha abre <c>/media/{grupo}/{seu id}</c> e envia
/// os pacotes; quem assiste abre <c>/media/{grupo}/{id de quem compartilha}</c> e os recebe.
/// </summary>
/// <remarks>
/// Cada mensagem binária é um pacote: 1 byte de tipo, 1 byte de flags, 8 bytes de carimbo de tempo
/// (microssegundos, little-endian) e o conteúdo. Vídeo é H.264 Annex B (SPS/PPS antes de cada quadro-chave);
/// áudio é Opus, 48 kHz estéreo, 20 ms por pacote.
/// </remarks>
public static class MediaContract
{
    public const string Path = "/media";

    public const int HeaderSize = 10;

    /// <summary>Maior pacote aceito (um quadro-chave em 1080p fica bem abaixo disso).</summary>
    public const int MaxPacketSize = 4 * 1024 * 1024;

    public const byte Video = 1;

    public const byte Audio = 2;

    /// <summary>API → quem compartilha: alguém começou a assistir ou perdeu pacotes e precisa de um quadro-chave.</summary>
    public const byte KeyframeRequest = 3;

    public const byte KeyframeFlag = 1;

    public static Uri BuildUri(Uri apiBaseUri, Guid groupId, Guid sharerId)
    {
        var builder = new UriBuilder(new Uri(apiBaseUri, $"{Path.TrimStart('/')}/{groupId}/{sharerId}"));
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    public static void WriteHeader(Span<byte> destination, byte type, byte flags, long timestampMicroseconds)
    {
        destination[0] = type;
        destination[1] = flags;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(destination[2..], timestampMicroseconds);
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out byte type, out byte flags, out long timestampMicroseconds)
    {
        if (packet.Length < HeaderSize)
        {
            type = flags = 0;
            timestampMicroseconds = 0;
            return false;
        }

        type = packet[0];
        flags = packet[1];
        timestampMicroseconds = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(packet[2..]);
        return true;
    }
}

/// <summary>Nomes do hub de grupos em tempo real (SignalR), compartilhados entre a API e o aplicativo.</summary>
public static class GroupsHubContract
{
    public const string Path = "/hubs/groups";

    public const int MaxNameLength = 40;

    public const int MaxMembers = 20;

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

    /// <summary>Cliente → servidor: <see cref="ScreenShareStart"/>; começa a compartilhar a tela na sala atual. Retorna o <see cref="ScreenShareDto"/> ou null.</summary>
    public const string StartScreenShare = "StartScreenShare";

    /// <summary>Cliente → servidor: encerra o compartilhamento de tela desta conexão.</summary>
    public const string StopScreenShare = "StopScreenShare";
}
