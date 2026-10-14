# ScreenCord

Sistema com dois componentes:

- **Aplicativo desktop** (C# / .NET 10 / WPF): conecta uma conta do Discord via OAuth2 e exibe o perfil.
- **API central** (ASP.NET Core / .NET 10 / SQLite): cadastra quem entra com o Discord e mantém as amizades **entre usuários deste sistema**.

> **Importante:** os "amigos" aqui são do ScreenCord, não a sua lista de amigos do Discord. A API oficial do Discord não permite ler a lista de amigos (veja [Limitações reais da API do Discord](#limitações-reais-da-api-do-discord)). Para alguém aparecer na busca, essa pessoa precisa ter entrado neste sistema com a conta dela.

## O que funciona

- Login pelo navegador com **OAuth2 Authorization Code + PKCE (S256)**, como cliente público (sem `client_secret`).
- Perfil via `GET /users/@me`: nome de exibição, nome de usuário, ID, avatar e URL do avatar.
- **Cadastro automático**: ao conectar, o app faz login na API central, que cria o usuário (ou atualiza nome e avatar) com os dados confirmados pelo Discord.
- **Amigos no sistema**: buscar usuários por nome (`@fulano`), enviar solicitação, aceitar, recusar, cancelar e remover amigo.
- Sessão do Discord reutilizada entre execuções (tokens cifrados com DPAPI), com renovação automática.
- Tratamento de falta de conexão, timeout, token inválido, autorização negada, HTTP 429 e indisponibilidade — tanto do Discord quanto da API central.

## Requisitos

- Windows 10/11 x64 (aplicativo). A API roda em qualquer sistema com .NET 10.
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

Para testar a amizade entre duas contas na mesma máquina, entre com a primeira, clique em **Desconectar**, entre com a segunda, busque a primeira pelo nome de usuário e envie a solicitação.

## 4. Executar os testes

```bash
dotnet test ScreenCord.sln
```

Nenhum teste acessa o Discord: as respostas são simuladas. Os testes da API sobem o servidor real em memória, com SQLite em memória.

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

`relationship` é `None`, `Friend`, `PendingOutgoing` ou `PendingIncoming`. Erros retornam `{ "code": "...", "message": "..." }`.

## Arquitetura

```
src/ScreenCord            Aplicativo WPF
├── Models/          DTOs do Discord e modelos de exibição
├── Services/        OAuth2, API do Discord, perfil, cliente da API central (FriendsService)
├── Security/        PKCE, sessão do Discord e armazenamento DPAPI
├── Infrastructure/  Opções, DI, erros centralizados, rate limit, navegador
├── ViewModels/      Estado e comandos (MVVM)
├── Views/ Themes/   Janela e tema escuro
src/ScreenCord.Api        API central
├── Auth/            Verificação do token do Discord e autenticação por sessão
├── Data/            EF Core: Users, Friendships, Sessions
└── Endpoints/       Rotas de login, busca e amizades
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
