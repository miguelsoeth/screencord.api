# ScreenCord

Sistema com dois componentes:

- **Aplicativo desktop** (C# / .NET 10 / WPF): conecta uma conta do Discord via OAuth2 e exibe o perfil.
- **API central** (ASP.NET Core / .NET 10 / SQLite): cadastra quem entra com o Discord e mantém as amizades **entre usuários deste sistema**.

> **Importante:** os "amigos" aqui são do ScreenCord, não a sua lista de amigos do Discord. A API oficial do Discord não permite ler a lista de amigos (veja [Limitações reais da API do Discord](#limitações-reais-da-api-do-discord)). Para alguém aparecer na busca, essa pessoa precisa ter entrado neste sistema com a conta dela.

## O que funciona

- Login pelo navegador com **OAuth2 Authorization Code + PKCE (S256)**, como cliente público (sem `client_secret`).
- Perfil via `GET /users/@me`: nome de exibição, nome de usuário, ID, avatar e URL do avatar.
- **Cadastro automático**: ao conectar, o app faz login na API central, que cria o usuário (ou atualiza nome e avatar) com os dados confirmados pelo Discord.
- **Amigos no sistema**: no menu do botão de amigos (ao lado de atualizar perfil, com uma bolinha vermelha quando há solicitações recebidas), buscar usuários por nome (`@fulano`), enviar solicitação, aceitar, recusar, cancelar e remover amigo (com confirmação).
- **Grupos (salas de presença em tempo real)**: lista compacta na lateral direita, uma linha por grupo com nome, indicador de conexão e a contagem `conectados/integrantes` (ex.: `1/2`). Quem está conectado aparece listado (foto e nome) logo abaixo do grupo; a seta à esquerda recolhe ou expande essa lista. Clicar no nome conecta você à sala; clicar na contagem abre os integrantes (foto, nome e quem está conectado) e permite adicionar amigos. Qualquer integrante adiciona os próprios amigos ou sai do grupo; só quem criou o grupo vê **Excluir grupo**, que o apaga para todos (com confirmação). Entradas e saídas aparecem na hora para todos.
- **Compartilhamento de tela**: na sala em que você está, o botão de compartilhar (ao lado de "Sair da sala") abre a escolha de **tela inteira ou janela**, **resolução** (540p, 720p, 1080p), **taxa de quadros** (30 ou 60 fps), **taxa de bits** (slide de 2000 a 7000 kbps, de 500 em 500) e **compartilhar áudio** ou não. O padrão é 720p, 30 fps, 4000 kbps, com áudio; a última escolha é lembrada enquanto o app está aberto. Durante o compartilhamento, a engrenagem ao lado de parar abre a mesma escolha para **alterar as configurações** (ou a tela/janela): o compartilhamento recomeça por baixo dos panos e quem está assistindo continua vendo, já com a nova configuração. Quem compartilha aparece com o selo **AO VIVO** na lista de conectados; clicar nele mostra a transmissão na área principal da janela, uma de cada vez (abrir outra troca a atual; o **X** da barra para de assistir), com volume, mudo e **tela cheia** (botão, F11 ou duplo clique; Esc, F11 ou duplo clique para sair). Clicar em AO VIVO de outro grupo entra na sala dele antes. Sair da sala, trocar de sala ou perder a conexão encerra o compartilhamento e fecha a transmissão que você assistia.
- Sessão do Discord reutilizada entre execuções (tokens cifrados com DPAPI), com renovação automática.
- Tratamento de falta de conexão, timeout, token inválido, autorização negada, HTTP 429 e indisponibilidade — tanto do Discord quanto da API central.

## Requisitos

- Windows 10 (versão 2004 ou mais recente) ou Windows 11, x64 (aplicativo). A API roda em qualquer sistema com .NET 10.
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)

## 1. Configurar o aplicativo no Discord Developer Portal

1. Acesse <https://discord.com/developers/applications> e clique em **New Application**.
2. Em **General Information**, copie o **Application ID** (é o *Client ID*; não é um segredo).
3. Na aba **OAuth2**:
   - Em **Redirects**, adicione exatamente `http://127.0.0.1:47821/callback`.
   - Ative **Public Client** (permite trocar o código com PKCE, sem `client_secret`).
   - Salve.
4. **Não** use o *Client Secret* nem a *Public Key*: nenhum dos dois é necessário.

O único escopo solicitado é `identify`.

## 2. Configurar o Client ID

O mesmo Application ID vai nos dois componentes, em arquivos `appsettings.Local.json` (ignorados pelo Git):

`src/ScreenCord/appsettings.Local.json` (aplicativo):

```json
{
  "Discord": { "ClientId": "SEU_APPLICATION_ID" }
}
```

`src/ScreenCord.Api/appsettings.Local.json` (API):

```json
{
  "Discord": { "ClientId": "SEU_APPLICATION_ID" }
}
```

A API usa o Client ID para recusar tokens do Discord emitidos para outros aplicativos.

Opções do aplicativo:

| Chave | Padrão | Descrição |
|---|---|---|
| `Discord:ClientId` | *(vazio)* | Application ID. Obrigatório. |
| `Discord:RedirectPort` | `47821` | Porta do callback local. Se mudar, atualize o redirect no portal. |
| `Discord:Scopes` | `identify` | Escopos separados por espaço. |
| `Discord:LoginTimeoutSeconds` | `300` | Espera máxima pela autorização no navegador. |
| `Api:BaseUrl` | `http://localhost:5080` | Endereço da API central. Precisa ser `https`, exceto para localhost. |

Opções da API:

| Chave | Padrão | Descrição |
|---|---|---|
| `Discord:ClientId` | *(vazio)* | Application ID. Obrigatório. |
| `ConnectionStrings:Default` | `Data Source=screencord.db` | Banco SQLite. |
| `Session:LifetimeDays` | `30` | Validade da sessão da API. |

## 3. Executar em modo Debug

### Painel de desenvolvimento (recomendado)

```bash
powershell -ExecutionPolicy Bypass -File .\screencord.ps1
```

Menu navegável com as setas (↑/↓, Enter, Esc) para subir, parar e recompilar a API e o app, acompanhar os logs ao vivo e rodar os testes. Os processos rodam em segundo plano e continuam vivos ao fechar o menu; PIDs e logs ficam em `.screencord/`.

Também aceita ações diretas: `status`, `start-api`, `stop-api`, `start-app`, `stop-app`, `start-all`, `stop-all`, `build`, `rebuild`, `test`, `logs-api`, `logs-app`. Exemplo: `.\screencord.ps1 start-all`.

### Manualmente

Em um terminal, a API (fica em `http://localhost:5080`; o banco é criado na primeira execução):

```bash
dotnet run --project src/ScreenCord.Api
```

Em outro, o aplicativo:

```bash
dotnet run --project src/ScreenCord
```

Sem a API no ar, o perfil continua funcionando e a seção "Amigos" mostra um erro com o botão "Tentar novamente".

Para testar os grupos em tempo real, abra duas instâncias do aplicativo com contas diferentes que sejam amigas, crie um grupo em uma, adicione a outra pela contagem de integrantes e clique no nome do grupo nas duas. Para testar o compartilhamento de tela, clique no botão de compartilhar em uma delas e no selo AO VIVO na outra.

Para testar a amizade entre duas contas na mesma máquina, entre com a primeira, clique em **Desconectar**, entre com a segunda, busque a primeira pelo nome de usuário e envie a solicitação.

## 4. Executar os testes

```bash
dotnet test ScreenCord.sln
```

Nenhum teste acessa o Discord: as respostas são simuladas. Os testes da API sobem o servidor real em memória, com SQLite em memória. Os testes de mídia codificam e decodificam vídeo de verdade com o Media Foundation do Windows (usando o encoder de hardware, se houver).

## 5. Publicar

Aplicativo para Windows x64 (executável único, sem exigir .NET instalado):

```bash
dotnet publish src/ScreenCord -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/app
```

Distribua `ScreenCord.exe` com `appsettings.json` contendo o `Discord:ClientId` e o `Api:BaseUrl` do servidor.

API:

```bash
dotnet publish src/ScreenCord.Api -c Release -o publish/api
```

Antes de expor a API fora da sua máquina:

- Sirva **somente por HTTPS** (o app recusa `http` que não seja localhost). O token do Discord trafega até a API no login.
- O proxy reverso precisa repassar WebSockets (`/hubs/groups` e `/media/...`) e não deve guardar as respostas em buffer, senão o vídeo atrasa. Conte com a banda de saída do compartilhamento de tela (taxa × espectadores).
- Atrás de proxy reverso, configure *forwarded headers*, senão o limite de requisições por IP enxerga só o IP do proxy.
- O banco é criado com `EnsureCreated`. Para evoluir o esquema sem perder dados, adote migrações do EF Core. Para mais de uma instância, troque o SQLite por um banco servidor.
- Faça backup do arquivo `screencord.db`.

## API central

Com a API rodando em Development (o padrão do `screencord.ps1` e do `dotnet run`), a documentação interativa fica em:

- Swagger UI: <http://localhost:5080/swagger>
- Documento OpenAPI: <http://localhost:5080/openapi/v1.json>

Fora de Development as duas rotas não são publicadas.

Todas as rotas, exceto `/health` e `/auth/discord`, exigem `Authorization: Bearer <token de sessão>`.

| Método e rota | Descrição |
|---|---|
| `GET /health` | Verificação de disponibilidade. |
| `POST /auth/discord` | Corpo `{ "accessToken": "<token do Discord>" }`. Confirma o token com o Discord, cria/atualiza o usuário e devolve `{ token, expiresAtUtc, user }`. |
| `POST /auth/logout` | Encerra a sessão atual. |
| `GET /me` | Usuário da sessão. |
| `GET /users/search?q=` | Busca por nome de usuário ou de exibição (mínimo 2 caracteres, até 20 resultados, sem o próprio usuário). Cada item traz `relationship`. |
| `GET /friends` | Amigos confirmados. |
| `DELETE /friends/{userId}` | Desfaz a amizade. |
| `GET /friends/requests` | Solicitações `incoming` e `outgoing`. |
| `POST /friends/requests` | Corpo `{ "userId": "<guid>" }`. Envia a solicitação; se a outra pessoa já tinha pedido, confirma a amizade. |
| `POST /friends/requests/{userId}/accept` | Aceita a solicitação recebida de `userId`. |
| `DELETE /friends/requests/{userId}` | Recusa uma solicitação recebida ou cancela uma enviada. |
| `GET /groups` | Seus grupos, com `memberCount`, `connectedUsers` (id, nome e avatar de quem está na sala) e `presenceVersion`. |
| `POST /groups` | Corpo `{ "name": "..." }` (1 a 40 caracteres). Cria o grupo com você como integrante. |
| `GET /groups/{groupId}` | Integrantes, cada um com `isConnected`. Só para integrantes. |
| `POST /groups/{groupId}/members` | Corpo `{ "userId": "<guid>" }`. Adiciona um amigo seu (limite de 20 integrantes). |
| `DELETE /groups/{groupId}/members/me` | Sai do grupo; o grupo é apagado quando o último integrante sai. |
| `DELETE /groups/{groupId}` | Exclui o grupo para todos (só quem criou; 403 `not_group_owner` para os outros). Todos saem da sala, os compartilhamentos terminam e cada integrante recebe `GroupRemoved`. `GET /groups/{groupId}` traz `canDelete`. |

`relationship` é `None`, `Friend`, `PendingOutgoing` ou `PendingIncoming`. Erros retornam `{ "code": "...", "message": "..." }`.

### Tempo real (SignalR)

O hub `/hubs/groups` mantém uma conexão persistente (WebSocket, com fallback automático do SignalR), autenticada com o mesmo token de sessão no cabeçalho `Authorization` — o token nunca vai na URL.

| Mensagem | Direção | Descrição |
|---|---|---|
| `JoinGroup(groupId)` | app → API | Entra na sala (saindo da anterior). Retorna a presença atual, ou `null` se você não for integrante. |
| `LeaveGroup()` | app → API | Sai da sala atual. Fechar a conexão tem o mesmo efeito. |
| `PresenceChanged(presence)` | API → app | `{ groupId, connectedUsers, version }`, enviado a todos os integrantes a cada entrada ou saída. |
| `GroupChanged(group)` | API → app | Grupo criado ou com integrantes alterados (inclusive para quem acabou de ser adicionado). |
| `GroupRemoved(groupId)` | API → app | Você saiu do grupo (avisa as suas outras janelas). |
| `StartScreenShare(start)` | app → API | `{ width, height, frameRate, bitrateKbps, hasAudio }`. Começa a compartilhar na sala atual; retorna `{ userId, width, height, frameRate, bitrateKbps, hasAudio }`, ou `null` se a conexão não estiver numa sala ou a configuração não for uma das opções (altura até 1080, 30/60 fps, 2000 a 7000 kbps de 500 em 500). |
| `StopScreenShare()` | app → API | Encerra o compartilhamento desta conexão. Sair da sala, trocar de sala ou desconectar também encerra. |

A presença (`PresenceChanged` e os grupos de `GET /groups`) traz também `screenShares`: quem está compartilhando na sala e com que configuração.

`version` cresce a cada mudança de presença (inclusive início e fim de compartilhamento); o app descarta notificações mais antigas que a última aplicada, então mensagens fora de ordem não deixam a lista errada. Ao reconectar, o app recarrega os grupos e volta para a sala em que estava. A presença fica em memória numa instância da API; para rodar várias instâncias seria preciso um backplane (ex.: Redis).

### Compartilhamento de tela

```
quem compartilha                          API                              quem assiste
Windows Graphics Capture ─┐
  (tela ou janela)        │ GPU: BGRA → NV12, escala   WebSocket /media/{grupo}/{id}   WebSocket /media/{grupo}/{id}
H.264 (Media Foundation) ─┼──────────────────────────► repasse sem decodificar ───────► H.264 → BGRA → janela
Opus (áudio por processo)─┘                             (uma fila por espectador)         Opus → WASAPI
```

- **Captura**: Windows Graphics Capture (tela ou janela, com o cursor; no Windows 11, sem a borda amarela). A conversão para NV12 e a mudança de tamanho são feitas na GPU (processador de vídeo do Direct3D 11). A largura segue a proporção da tela ou janela; a altura é a resolução escolhida, sem ampliar origens menores. Se a janela mudar de tamanho, a imagem é encaixada com faixas pretas.
- **Vídeo**: H.264 com taxa de bits constante, baixa latência e sem quadros B, pelo encoder de hardware da placa (NVIDIA, Intel, AMD) quando existe, com o encoder por software do Windows como alternativa. Quadro-chave a cada 4 s e **sob demanda**: quando alguém começa a assistir, a API pede um quadro-chave a quem compartilha, e a imagem aparece em uma fração de segundo. Com a tela parada, o ritmo cai para 2 quadros por segundo (1 s depois da última mudança), em vez de reenviar o mesmo quadro na taxa cheia.
- **Áudio**: Opus 48 kHz estéreo a 128 kbps, em pacotes de 20 ms. Tela inteira: tudo o que toca no computador **menos o próprio ScreenCord** (quem compartilha e também assiste alguém não devolve o áudio da outra pessoa). Janela: só o áudio do programa dono da janela (e dos processos filhos dele, ex.: navegadores). Usa a captura de áudio por processo do Windows 10 2004+; em versões anteriores, a tela inteira captura o dispositivo padrão inteiro e a janela fica sem áudio.
- **Canal de mídia**: um WebSocket por compartilhamento em `/media/{groupId}/{sharerId}`, autenticado pelo mesmo cabeçalho `Authorization` (o token nunca vai na URL). Quem compartilha conecta no próprio id depois de `StartScreenShare` (antes disso recebe 409); quem assiste conecta no id de quem compartilha e precisa estar na sala (403 se não estiver). Cada mensagem binária é um pacote: 1 byte de tipo (1 vídeo, 2 áudio, 3 pedido de quadro-chave, 4 pausar o vídeo, 5 retomar o vídeo), 1 byte de flags (bit 0 = quadro-chave), 8 bytes de carimbo de tempo em µs (little-endian) e o conteúdo. Vídeo vai em H.264 Annex B com SPS/PPS antes de cada quadro-chave; pacotes acima de 4 MB são recusados.
- **Rede lenta**: a API mantém uma fila por espectador com cerca de 1 s de vídeo. Se ela enche, o espectador perde quadros até o próximo quadro-chave (pedido na hora) em vez de acumular atraso, sem afetar os outros. Do lado de quem compartilha vale o mesmo para a fila de envio. Quem assiste e fica sem imagem pede outro quadro-chave sozinho.
- **Janela minimizada**: quem minimiza o ScreenCord enquanto assiste avisa a API, que para de mandar o vídeo para essa pessoa e mantém só o áudio. Ao restaurar, o vídeo volta pelo próximo quadro-chave, pedido na hora.
- **Bitrate adaptativo**: a qualidade escolhida é o teto. Se a fila de envio de quem compartilha não esvazia (ou estoura), a taxa do encoder cai 30% (até 1/5 da escolhida) e volta a subir aos poucos quando a rede folga. Só olha a conexão de quem compartilha: um espectador com rede ruim continua perdendo quadros sem baixar a qualidade dos outros.

Limitações: a mídia passa pela API, então o tráfego de saída do servidor é a taxa escolhida vezes o número de espectadores (ex.: 5000 kbps para 4 pessoas ≈ 20 Mbps). Para muitos espectadores, o caminho seria WebRTC com um SFU. Conteúdo protegido (ex.: vídeos com DRM em alguns navegadores) aparece preto, como em qualquer captura do Windows. Janelas minimizadas não geram imagem até serem restauradas.

## Arquitetura

```
src/ScreenCord            Aplicativo WPF
├── Models/          DTOs do Discord e modelos de exibição
├── Services/        OAuth2, API do Discord, perfil, cliente da API central (amigos, grupos), conexão SignalR e compartilhamento de tela
├── Media/           Captura de tela, H.264 (Media Foundation), áudio Opus/WASAPI e canal de mídia
├── Security/        PKCE, sessão do Discord e armazenamento DPAPI
├── Infrastructure/  Opções, DI, erros centralizados, rate limit, navegador
├── ViewModels/      Estado e comandos (MVVM)
├── Views/ Themes/   Janela e tema escuro
src/ScreenCord.Api        API central
├── Auth/            Verificação do token do Discord e autenticação por sessão
├── Data/            EF Core: Users, Friendships, Sessions, Groups, GroupMembers
├── Endpoints/       Rotas de login, busca, amizades e grupos; canal de mídia (WebSocket)
└── Realtime/        Hub SignalR de grupos, rastreador de presença e repasse da mídia
src/ScreenCord.Contracts  DTOs compartilhados entre app e API
tests/ScreenCord.Tests, tests/ScreenCord.Api.Tests
```

Como o login chega à API:

1. O app obtém o access token do Discord (PKCE, no navegador).
2. O app envia esse token a `POST /auth/discord`.
3. A API chama `GET /oauth2/@me` no Discord, que informa o usuário **e para qual aplicativo o token foi emitido**. Tokens de outro aplicativo, sem o escopo `identify` ou inválidos são recusados.
4. A API cria ou atualiza o usuário e devolve um token de sessão próprio. O token do Discord não é armazenado.
5. O app usa só o token de sessão nas demais chamadas e o mantém apenas em memória; ao reabrir, repete o passo 2 silenciosamente.

## Segurança

- A senha do Discord nunca passa pelo aplicativo nem pela API.
- Cliente público com PKCE; nenhum `client_secret` em lugar nenhum.
- `state` aleatório de 256 bits, comparado em tempo constante.
- Callback local só em `127.0.0.1`, só `GET` no caminho configurado, página estática, encerrado ao fim do login.
- Tokens do Discord persistidos somente cifrados com DPAPI. Eles são enviados apenas para `discord.com` e, no login, para a API central configurada (HTTPS obrigatório fora de localhost).
- Na API: tokens de sessão opacos de 256 bits, guardados só como hash SHA-256; verificação do aplicativo de origem do token do Discord; limite de requisições (10 logins/min por IP, 120 req/min por usuário); curingas da busca tratados como texto; índice único por par de usuários contra amizades duplicadas.
- Tokens e códigos de autorização não são gravados em log.
- Dados recebidos (do Discord e da API) são validados antes de irem para a tela ou para URLs.

## Privacidade

Qualquer usuário autenticado pode encontrar qualquer outro usuário do sistema pela busca (nome de usuário, nome de exibição, ID do Discord e avatar). Informe isso a quem for usar o sistema. A API guarda somente esses dados de perfil, as datas de criação/último login e as relações de amizade.

## Limitações reais da API do Discord

Verificado na documentação oficial (<https://docs.discord.com/developers>) em outubro de 2026:

- **Não há endpoint HTTP oficial para listar amigos via OAuth2.** O escopo `relationships.read` pertence ao Discord Social SDK (SDK nativo voltado a jogos) e não é utilizável aqui. Por isso este sistema mantém a própria relação de amigos.
- Endpoints internos do cliente Discord, tokens de usuário, scraping e automação de interface violam os termos do Discord e **não** são usados.
- **PKCE** não aparece na página principal de OAuth2; o modo de cliente público está descrito na documentação do Social SDK (opção *Public Client*). Sem ela, a troca do código falha com `invalid_client`.
- A renovação por refresh token sem `client_secret` depende do mesmo modo. Se o Discord recusar, a sessão local é apagada e o app volta para "Conectar com Discord".
- Avatares animados são exibidos como imagem estática (PNG).
