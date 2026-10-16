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

/// <param name="IsOwner">O integrante é quem criou o grupo.</param>
public sealed record GroupMemberDto(
    Guid Id,
    string DiscordId,
    string Username,
    string? GlobalName,
    string? Discriminator,
    string? AvatarHash,
    bool IsConnected,
    bool IsOwner = false);

/// <param name="CanDelete">Quem pediu os detalhes pode excluir o grupo (só quem o criou).</param>
public sealed record GroupDetailsDto(Guid Id, string Name, IReadOnlyList<GroupMemberDto> Members, long PresenceVersion, bool CanDelete = false);

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
/// A largura acompanha a proporção da tela ou janela; a altura é a resolução escolhida. O que cada plano
/// permite está em <see cref="PlanLimits"/>; os métodos sem plano valem para o gratuito (540p a 60 fps ou
/// 720p a 30 fps, até 3000 kbps), que é o que as versões do aplicativo anteriores aos planos conhecem.
/// </summary>
public static class ScreenShareLimits
{
    /// <summary>Alturas oferecidas no plano gratuito.</summary>
    public static readonly int[] Heights = [540, 720];

    public static readonly int[] FrameRates = [30, 60];

    /// <summary>Maior altura a 60 fps no plano gratuito; acima dela, só 30 fps.</summary>
    public const int MaxHeightAt60Fps = 540;

    /// <summary>Teto da taxa de bits no plano gratuito. O aplicativo não deixa escolher: adapta a taxa à rede, até o teto do plano.</summary>
    public const int MaxBitrateKbps = 3000;

    public const int MinBitrateKbps = 300;

    public static bool IsValidBitrate(int kbps) => PlanLimits.Free.AllowsBitrate(kbps);

    /// <summary>A altura e o fps estão dentro do plano gratuito (a altura real pode ser menor, numa janela pequena).</summary>
    public static bool IsValidQuality(int height, int frameRate) => PlanLimits.Free.AllowsQuality(height, frameRate);

    /// <summary>Janelas muito largas (ex.: monitores ultrawide) podem passar de 16:9.</summary>
    public const int MaxWidth = 3840;

    public const int MinSize = 64;

    public static bool IsValid(ScreenShareStart? start) => IsValid(start, PlanLimits.Free);

    public static bool IsValid(ScreenShareStart? start, PlanLimits limits) =>
        start is not null &&
        start.Width is >= MinSize and <= MaxWidth && start.Width % 2 == 0 &&
        start.Height >= MinSize && start.Height % 2 == 0 &&
        limits.AllowsQuality(start.Height, start.FrameRate) &&
        limits.AllowsBitrate(start.BitrateKbps);
}

[JsonConverter(typeof(JsonStringEnumConverter<PlanTier>))]
public enum PlanTier
{
    Free,
    Pro,
}

/// <summary>
/// O que um plano permite. A API valida com os limites do plano de cada conta e os informa em
/// <see cref="PlanDto"/>; o aplicativo só habilita as opções que cabem neles.
/// </summary>
/// <param name="MaxHeight">Maior altura do compartilhamento de tela.</param>
/// <param name="MaxHeightAt60Fps">Maior altura a 60 fps; acima dela, só 30 fps.</param>
/// <param name="MaxBitrateKbps">Teto da taxa de bits (a taxa começa nele e se adapta à rede).</param>
/// <param name="MaxMembers">Integrantes por grupo. Vale o plano de quem criou o grupo.</param>
/// <param name="MaxCreatedGroups">Grupos criados pela conta ao mesmo tempo.</param>
public sealed record PlanLimits(int MaxHeight, int MaxHeightAt60Fps, int MaxBitrateKbps, int MaxMembers, int MaxCreatedGroups)
{
    public static PlanLimits Free { get; } = new(
        720, ScreenShareLimits.MaxHeightAt60Fps, ScreenShareLimits.MaxBitrateKbps, GroupsHubContract.MaxMembers, GroupsHubContract.MaxCreatedGroups);

    /// <summary>720p60, 1080p30 e 1080p60, até 5 Mbps; o dobro de integrantes; grupos "ilimitados" (teto alto contra abuso).</summary>
    public static PlanLimits Pro { get; } = new(1080, 1080, 5000, 20, 100);

    public static PlanLimits For(PlanTier tier) => tier == PlanTier.Pro ? Pro : Free;

    /// <summary>A altura e o fps cabem no plano (a altura real pode ser menor que a escolhida, numa janela pequena).</summary>
    public bool AllowsQuality(int height, int frameRate) =>
        height <= MaxHeight && ScreenShareLimits.FrameRates.Contains(frameRate) && (frameRate <= 30 || height <= MaxHeightAt60Fps);

    public bool AllowsBitrate(int kbps) => kbps >= ScreenShareLimits.MinBitrateKbps && kbps <= MaxBitrateKbps;
}

/// <summary>Plano da conta, em <c>GET /me/plan</c> e no evento <see cref="GroupsHubContract.PlanChanged"/>.</summary>
/// <param name="ProUntilUtc">Até quando o Pro vale; null no gratuito ou no Pro concedido sem prazo.</param>
/// <param name="Offers">Períodos de Pro à venda; vazio quando a venda não está disponível.</param>
public sealed record PlanDto(PlanTier Tier, DateTimeOffset? ProUntilUtc, PlanLimits Limits, IReadOnlyList<ProOfferDto> Offers);

/// <summary>Um período de Pro à venda (pagamento único, sem renovação automática).</summary>
public sealed record ProOfferDto(string Id, string Name, int Days, int PriceCents);

/// <summary>Corpo de <c>POST /billing/checkout</c>.</summary>
public sealed record ProCheckoutCreate(string OfferId);

/// <summary>Página de pagamento, aberta no navegador. O Pro é ativado quando o pagamento é confirmado (pelo hub, <see cref="GroupsHubContract.PlanChanged"/>).</summary>
public sealed record ProCheckoutDto(string Url);

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

    /// <summary>Quem assiste → API: parou de olhar (ex.: minimizou a janela); a API para de mandar vídeo e mantém o áudio.</summary>
    public const byte PauseVideo = 4;

    /// <summary>Quem assiste → API: voltou a olhar; o vídeo recomeça no próximo quadro-chave, pedido na hora.</summary>
    public const byte ResumeVideo = 5;

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

    /// <summary>Integrantes por grupo no plano gratuito (<see cref="PlanLimits"/>).</summary>
    public const int MaxMembers = 10;

    /// <summary>Quantos grupos cada conta do plano gratuito pode ter criado ao mesmo tempo (participar de grupos de outros não conta).</summary>
    public const int MaxCreatedGroups = 5;

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

    /// <summary>Servidor → cliente (sem argumentos): amigos ou solicitações de amizade do usuário mudaram; recarregue as listas.</summary>
    public const string FriendsChanged = "FriendsChanged";

    /// <summary>Servidor → cliente: <see cref="PlanDto"/> novo da conta (Pro ativado, renovado ou estornado).</summary>
    public const string PlanChanged = "PlanChanged";

    /// <summary>Cliente → servidor: <see cref="ScreenShareStart"/>; começa a compartilhar a tela na sala atual. Retorna o <see cref="ScreenShareDto"/> ou null.</summary>
    public const string StartScreenShare = "StartScreenShare";

    /// <summary>Cliente → servidor: encerra o compartilhamento de tela desta conexão.</summary>
    public const string StopScreenShare = "StopScreenShare";
}

/// <summary>
/// Versão do aplicativo em cada requisição (HTTP, hub e mídia). A API recusa versões abaixo da mínima
/// com 426 e <see cref="OutdatedCode"/>; o aplicativo então se atualiza.
/// </summary>
public static class ClientVersionContract
{
    /// <summary>Cabeçalho com a versão SemVer do aplicativo (ex.: <c>0.2.0</c>).</summary>
    public const string Header = "X-ScreenCord-Version";

    /// <summary>Código do <see cref="ErrorResponse"/> quando a versão do aplicativo não é mais aceita.</summary>
    public const string OutdatedCode = "client_outdated";

    /// <summary>Página pública de download do aplicativo.</summary>
    public const string DownloadPage = "https://miguelsoeth.github.io/screencord/";
}
