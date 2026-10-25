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
    IReadOnlyList<ScreenShareDto>? ScreenShares = null,
    GroupSettings? Settings = null)
{
    [JsonIgnore]
    public IReadOnlyList<Guid> ConnectedUserIds => ConnectedUsers?.Select(u => u.Id).ToList() ?? [];
}

/// <summary>Tipo de transmissão numa sala: a tela (ou janela) do computador ou a câmera (webcam).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShareKind>))]
public enum ShareKind
{
    Screen,
    Camera,
}

/// <summary>
/// O que o grupo aceita transmitir na sala; quem criou o grupo muda. Quem não conhece o campo (versões antigas do
/// aplicativo, ou grupos criados antes dele) tem tudo liberado.
/// </summary>
public sealed record GroupSettings(bool AllowScreenShare = true, bool AllowCamera = true)
{
    public static GroupSettings Default { get; } = new();

    public bool Allows(ShareKind kind) => kind == ShareKind.Camera ? AllowCamera : AllowScreenShare;
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
/// <param name="Settings">O que o grupo aceita transmitir; ausente nas versões da API anteriores a ele (vale tudo liberado).</param>
/// <param name="CanEdit">Quem pediu os detalhes pode mudar as configurações do grupo (só quem o criou).</param>
public sealed record GroupDetailsDto(
    Guid Id,
    string Name,
    IReadOnlyList<GroupMemberDto> Members,
    long PresenceVersion,
    bool CanDelete = false,
    GroupSettings? Settings = null,
    bool CanEdit = false);

public sealed record GroupCreate(string Name);

public sealed record GroupMemberCreate(Guid UserId);

/// <summary>Corpo de <c>PUT /groups/{id}/settings</c>: só quem criou o grupo pode mudar.</summary>
public sealed record GroupSettingsUpdate(bool AllowScreenShare, bool AllowCamera)
{
    public GroupSettings ToSettings() => new(AllowScreenShare, AllowCamera);
}

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

/// <summary>
/// Pedido para começar a transmitir na sala em que a conexão está: a tela ou a câmera (<paramref name="Kind"/>; quem
/// não o envia está compartilhando a tela). O grupo precisa aceitar esse tipo (<see cref="GroupSettings"/>).
/// </summary>
public sealed record ScreenShareStart(int Width, int Height, int FrameRate, int BitrateKbps, bool HasAudio, ShareKind Kind = ShareKind.Screen);

/// <summary>Transmissão ativa numa sala (tela ou câmera). A mídia passa por <see cref="MediaContract.Path"/>.</summary>
public sealed record ScreenShareDto(Guid UserId, int Width, int Height, int FrameRate, int BitrateKbps, bool HasAudio, ShareKind Kind = ShareKind.Screen);

/// <summary>
/// Limites do compartilhamento de tela. A largura acompanha a proporção da tela ou janela; a altura é a resolução
/// escolhida. A qualidade depende do caminho da mídia (<see cref="StreamLimits"/>): pelo servidor (gratuito, o
/// padrão destas constantes) ou direto entre os computadores, nas salas locais do aplicativo.
/// </summary>
public static class ScreenShareLimits
{
    /// <summary>Alturas oferecidas pelo aplicativo; as acima do limite do caminho aparecem desligadas.</summary>
    public static readonly int[] Heights = [540, 720, 1080];

    public static readonly int[] FrameRates = [30, 60];

    /// <summary>Teto padrão da taxa de bits pelo servidor. O aplicativo não deixa escolher: adapta a taxa à rede, até o teto.</summary>
    public const int MaxBitrateKbps = 3000;

    public const int MinBitrateKbps = 300;

    /// <summary>Maior teto de taxa de bits que a API pode informar em <see cref="StreamLimits"/>.</summary>
    public const int MaxConfigurableBitrateKbps = 50_000;

    /// <summary>Janelas muito largas (ex.: monitores ultrawide) podem passar de 16:9.</summary>
    public const int MaxWidth = 3840;

    public const int MinSize = 64;

    public static bool IsValid(ScreenShareStart? start) => IsValid(start, StreamLimits.Relay);

    public static bool IsValid(ScreenShareStart? start, StreamLimits limits) =>
        start is not null &&
        start.Width is >= MinSize and <= MaxWidth && start.Width % 2 == 0 &&
        start.Height >= MinSize && start.Height % 2 == 0 &&
        limits.AllowsQuality(start.Height, start.FrameRate) &&
        limits.AllowsBitrate(start.BitrateKbps);
}

/// <summary>
/// Qualidade máxima de um caminho da mídia. Pelo servidor a banda é paga por quem mantém o ScreenCord, então é
/// limitada (<see cref="Relay"/>, ajustável na API e informada em <c>GET /limits</c>); direto entre os computadores
/// (<see cref="Direct"/>) quem paga é o upload de quem compartilha.
/// </summary>
/// <param name="MaxHeight">Maior altura (a real pode ser menor, numa janela pequena).</param>
/// <param name="Profiles">
/// Combinações de resolução e fps aceitas (ex.: 720p a 60 fps e 1080p a 30 fps, mas não 1080p a 60 fps). Quem não
/// as conhece (versões antigas do aplicativo) usa só o maior de cada, <paramref name="MaxHeight"/> e
/// <paramref name="MaxFrameRate"/>; sem elas, vale esse par.
/// </param>
public sealed record StreamLimits(int MaxHeight, int MaxFrameRate, int MaxBitrateKbps, IReadOnlyList<StreamProfile>? Profiles = null)
{
    /// <summary>Pelo servidor: 720p a 60 fps ou 1080p a 30 fps, até 3 Mbps. Ajustável no painel da API.</summary>
    public static StreamLimits Relay { get; } = FromProfiles([new(720, 60), new(1080, 30)], ScreenShareLimits.MaxBitrateKbps);

    /// <summary>Direto entre os computadores (salas locais): até 1080p a 60 fps, até 5 Mbps.</summary>
    public static StreamLimits Direct { get; } = new(1080, 60, 5000);

    public static StreamLimits FromProfiles(IReadOnlyList<StreamProfile> profiles, int maxBitrateKbps) =>
        new(profiles.Max(p => p.Height), profiles.Max(p => p.FrameRate), maxBitrateKbps, profiles);

    /// <summary>Cabe se alguma combinação aceita for igual ou maior nos dois (720p a 60 fps cobre 540p a 30 fps).</summary>
    public bool AllowsQuality(int height, int frameRate) =>
        ScreenShareLimits.FrameRates.Contains(frameRate) &&
        (Profiles is { Count: > 0 }
            ? Profiles.Any(p => height <= p.Height && frameRate <= p.FrameRate)
            : height <= MaxHeight && frameRate <= MaxFrameRate);

    public bool AllowsBitrate(int kbps) => kbps >= ScreenShareLimits.MinBitrateKbps && kbps <= MaxBitrateKbps;

    /// <summary>Os dados vêm de outra máquina: só limites dentro do que o aplicativo sabe fazer.</summary>
    public bool IsSane =>
        MaxHeight >= ScreenShareLimits.Heights.Min() && MaxHeight <= ScreenShareLimits.Heights.Max() &&
        ScreenShareLimits.FrameRates.Contains(MaxFrameRate) &&
        MaxBitrateKbps >= ScreenShareLimits.MinBitrateKbps && MaxBitrateKbps <= ScreenShareLimits.MaxConfigurableBitrateKbps &&
        (Profiles is null || Profiles is { Count: > 0 and <= 6 } && Profiles.All(p =>
            ScreenShareLimits.Heights.Contains(p.Height) && ScreenShareLimits.FrameRates.Contains(p.FrameRate)));

    // A lista de combinações compara pelo conteúdo, como o resto do registro.
    public bool Equals(StreamLimits? other) =>
        other is not null && MaxHeight == other.MaxHeight && MaxFrameRate == other.MaxFrameRate &&
        MaxBitrateKbps == other.MaxBitrateKbps && (Profiles ?? []).SequenceEqual(other.Profiles ?? []);

    public override int GetHashCode() => HashCode.Combine(MaxHeight, MaxFrameRate, MaxBitrateKbps, Profiles?.Count ?? 0);
}

/// <summary>Uma combinação de resolução (altura) e fps que o servidor aceita no compartilhamento.</summary>
public sealed record StreamProfile(int Height, int FrameRate);

/// <summary>Resposta de <c>GET /limits</c>: o que a API aceita, para o aplicativo só oferecer o que cabe.</summary>
public sealed record ServerLimitsDto(StreamLimits Relay);

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

    public const int MaxMembers = 10;

    /// <summary>Quantos grupos cada conta pode ter criado ao mesmo tempo (participar de grupos de outros não conta).</summary>
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

    /// <summary>
    /// Cliente → servidor: <see cref="ScreenShareStart"/>; começa a transmitir (tela ou câmera) na sala atual. Retorna o
    /// <see cref="ScreenShareDto"/> ou null (fora de sala, acima dos limites ou tipo que o grupo não aceita).
    /// </summary>
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
    public const string DownloadPage = "https://screencord.miguelsoeth.online/";
}

/// <summary>
/// Relatório de erros do aplicativo (<see cref="TelemetryContract.ErrorsPath"/>). Não leva tokens nem dados
/// de conta: <see cref="InstallId"/> é um id aleatório da instalação e <see cref="RunId"/>, desta execução.
/// </summary>
/// <param name="Device">
/// Computador, igual em toda a execução: placas de vídeo e driver, telas, processador, memória, idioma
/// (até <see cref="TelemetryContract.MaxContextEntries"/> pares). Ausente nas versões até 0.6.0.
/// </param>
public sealed record ClientErrorReport(
    string AppVersion,
    string? Os,
    string? Environment,
    Guid InstallId,
    Guid RunId,
    IReadOnlyList<ClientLogEntry> Entries,
    IReadOnlyDictionary<string, string>? Device = null);

/// <param name="Level">Nível do log (<c>Warning</c>, <c>Error</c>, <c>Critical</c>).</param>
/// <param name="Exception">Tipo, mensagem e pilha da exceção, quando houver.</param>
/// <param name="TraceId">Trace W3C da requisição à API que falhou (o mesmo que a API registra), quando houver.</param>
/// <param name="Context">
/// Estado do aplicativo no momento do erro: tela aberta, modo, tempo aberto, compartilhamento ativo (encoder,
/// resolução...). Até <see cref="TelemetryContract.MaxContextEntries"/> pares.
/// </param>
/// <param name="Breadcrumbs">
/// Últimas linhas do log (informação para cima) antes do erro, da mais antiga para a mais nova,
/// até <see cref="TelemetryContract.MaxBreadcrumbs"/>.
/// </param>
public sealed record ClientLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? Exception = null,
    string? TraceId = null,
    IReadOnlyDictionary<string, string>? Context = null,
    IReadOnlyList<string>? Breadcrumbs = null);

public static class TelemetryContract
{
    /// <summary><c>POST</c> com <see cref="ClientErrorReport"/>; aceita sem login (inclusive no "Usar sem conta").</summary>
    public const string ErrorsPath = "/telemetry/errors";

    /// <summary>Entradas por relatório; o resto é descartado.</summary>
    public const int MaxEntries = 50;

    /// <summary>Pares de <see cref="ClientErrorReport.Device"/> e de <see cref="ClientLogEntry.Context"/>; o resto é descartado.</summary>
    public const int MaxContextEntries = 30;

    /// <summary>Linhas de <see cref="ClientLogEntry.Breadcrumbs"/>; o resto é descartado.</summary>
    public const int MaxBreadcrumbs = 30;

    /// <summary>Cabeçalho de resposta da API com o trace da requisição (o mesmo do <c>traceparent</c> enviado).</summary>
    public const string TraceIdHeader = "X-Trace-Id";
}
