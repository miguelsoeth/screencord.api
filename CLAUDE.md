# ScreenCord API

API central do ScreenCord: ASP.NET Core (.NET 10), SQLite via EF Core, SignalR (`/hubs/groups`) e WebSockets de mídia (`/media/...`). O app desktop fica no repositório `miguelsoeth/screencord.app`. O README tem a documentação funcional (endpoints, hub, configuração local).

## Estrutura

- `src/ScreenCord.Api`: a API.
- `src/ScreenCord.Contracts`: DTOs compartilhados com o app. **Existe uma cópia idêntica no repo do app**: ao mudar um DTO, atualize os dois repositórios e trate como mudança de contrato (ver versionamento).
- `tests/ScreenCord.Api.Tests`: testes de integração (servidor real em memória, SQLite em memória, Discord simulado).
- `Dockerfile`: imagem de produção.
- `deploy/`: arquivos que vão para o VPS (`compose.yml`, `Caddyfile`, `deploy.sh`, `backup.sh`).
- `.github/workflows/`: `ci.yml`, `release.yml`, `deploy.yml`.

## Comandos

```bash
dotnet build ScreenCord.Api.sln
dotnet test ScreenCord.Api.sln          # sempre rode antes de commitar
dotnet run --project src/ScreenCord.Api # http://localhost:5080, Swagger em /swagger
```

## Fluxo de git

- Trunk-based: `main` sempre pronta para deploy.
- Trabalhe em branches curtas (`feat/...`, `fix/...`, `chore/...`) e integre na `main` por PR com squash merge. O `ci.yml` roda os testes em todo PR e push na `main`.
- A `main` é protegida pelo ruleset "main protegida" (Settings → Rules): **push direto é recusado** (inclusive para o dono do repo), só entra por PR com squash merge, o check `test` do CI precisa passar, e não pode haver force push nem exclusão da branch. Não é exigida aprovação de revisor. Fluxo: `git switch -c fix/x` → commit → `git push -u origin fix/x` → `gh pr create --fill` → `gh pr merge --squash --delete-branch` depois do CI verde. Se renomear o job `test` do `ci.yml`, atualize o ruleset, senão nenhum PR consegue ser mesclado.
- Mensagens no padrão Conventional Commits, em português: `feat: convites para grupos`, `fix: token expirado não renovava`, `chore: ...`, `docs: ...`, `refactor: ...`, `test: ...`. Mudança que quebra o contrato com o app: `feat!:` e explique no corpo.
- Nunca commite segredos. Configuração de produção fica só no VPS, em `/srv/screencord/api.env`. `appsettings.Local.json` é configuração local e não vai para a imagem (está no `.dockerignore`).

## Versionamento (SemVer por tag)

- A versão vem da tag git `vMAJOR.MINOR.PATCH`. Não edite a versão no `.csproj` (lá fica `0.0.0-dev`, usada só no desenvolvimento). O CI passa `-p:Version=` com a versão da tag.
- **MAJOR**: quebra de contrato com o app (remover ou renomear campo, endpoint ou método do hub; mudar semântica). Enquanto estiver em `0.x`, quebras sobem o MINOR.
- **MINOR**: funcionalidade nova compatível (endpoint novo, campo opcional novo).
- **PATCH**: correção sem mudança de contrato.
- A versão em produção aparece em `GET /health` → `{"status":"ok","version":"0.2.0","minClientVersion":"0.1.0"}`.

## Versões do app atendidas

- O app manda a versão em `X-ScreenCord-Version` (HTTP, hub e mídia; `ClientVersionContract` nos Contracts). `ClientVersionGate` recusa as abaixo de `Clients:MinVersion` (no `appsettings.json`, versionada junto com o código) com **426** e `ErrorResponse` de código `client_outdated`; o app então se atualiza sozinho pela última GitHub Release pública. `/health`, `/swagger` e `/openapi` ficam de fora.
- Sem o cabeçalho conta como `0.1.0` (a única versão publicada sem ele). `0.0.0-dev` (app rodando do código) é sempre aceito. Cabeçalho mal formado: 400.
- Para atender versões diferentes de forma diferente, use `HttpContext.GetClientVersion()` (null para build de desenvolvimento).
- Regra: a API atende todas as versões do app a partir da mínima. Prefira mudanças compatíveis (campos opcionais, endpoints e métodos do hub novos, sem remover os antigos). Quebra: primeiro aceite o formato antigo e o novo; depois que o app novo sair e os usuários se atualizarem, suba `Clients:MinVersion` e remova o antigo. Se não houver como aceitar os dois, suba a mínima na mesma release da mudança e publique o app logo depois. O passo a passo está no `CLAUDE.md` do app.
- Subir `Clients:MinVersion` muda o contrato: é release da API (MINOR enquanto em `0.x`). Em emergência dá para sobrescrever sem release com `Clients__MinVersion=...` no `api.env` do VPS e `docker compose up -d api`, mas depois leve o valor para o `appsettings.json` e tire do `api.env`.

## Release e deploy

Para lançar uma versão, a partir da `main` atualizada e com os testes passando:

```bash
git tag v0.2.0
git push origin v0.2.0
```

O `release.yml` então:
1. roda os testes;
2. gera a imagem e publica em `ghcr.io/miguelsoeth/screencord.api:<versão>` (e `:latest`);
3. cria a GitHub Release com notas geradas a partir dos commits;
4. chama o `deploy.yml`, que copia `deploy/*` para o VPS por SSH e roda `deploy.sh <versão>`;
5. apaga imagens antigas do GHCR, mantendo as 5 mais recentes (o GHCR privado grátis tem 500 MB).

O `deploy.sh` faz backup do SQLite, troca a imagem, espera o `/health` responder com a versão nova e, se não responder em ~60 s, **volta sozinho para a versão anterior** e falha o workflow.

**Rollback ou redeploy manual:** GitHub → Actions → Deploy → Run workflow, informando uma versão já publicada (ex.: `0.1.0`). Ou pela linha de comando: `gh workflow run deploy.yml -f version=0.1.0`.

Não crie tags para testar o pipeline: cada tag gera release, imagem e deploy em produção.

## Produção (VPS)

- Host: `154.16.50.20` (VPS Ubuntu). Sem domínio: o HTTPS usa certificado da Let's Encrypt **para o IP**, no perfil `shortlived` (~6 dias), renovado pelo Caddy. URL pública: `https://154.16.50.20`.
- Acesso ao servidor (SSH, usuários, chaves, firewall e endurecimento): no `CLAUDE.local.md`, fora do git. Este repositório é público: não documente aqui como entrar no servidor.
- Diretório `/srv/screencord` (dono `deploy`):
  - `compose.yml`, `Caddyfile`, `deploy.sh`, `backup.sh`: copiados do repo a cada deploy (edite no repo, não no servidor).
  - `.env`: `PUBLIC_IP` e `API_VERSION` (a versão atual; o `deploy.sh` reescreve).
  - `api.env`: configuração da API no formato `Secao__Chave=valor` (ex.: `Discord__ClientId`). Não está no git. Ao adicionar uma configuração nova à API, documente aqui e adicione no servidor antes do deploy. Opcional: `RelayLimits__MaxHeight`, `RelayLimits__MaxFrameRate` e `RelayLimits__MaxBitrateKbps` mudam a qualidade máxima pelo servidor (padrão 720, 30 e 2500; o app lê de `GET /limits`). A banda de saída é a taxa vezes o número de espectadores: a franquia do VPS é de 5 TB/mês e, ao estourar, a porta cai para 10 Mbps. `Observability__DashboardToken` liga o painel em `https://154.16.50.20/admin` (gere com `openssl rand -hex 32`; sem ele o painel não existe); opcionais `Observability__RetentionDays` e `Observability__MaxEvents`.
  - `data/screencord.db`: banco SQLite em modo WAL. A pasta e o banco são do `deploy` (uid 1000), e o container da API roda com esse uid (`user: "1000:1000"` no `compose.yml`). Não mude o dono: o backup, que roda como `deploy`, precisa escrever o `-shm`.
  - `backups/`: backup diário às 03:15 (cron do `deploy`, guarda 7 dias) e um backup antes de cada deploy (guarda os 10 últimos).
- Containers: `api` (porta 8080 publicada só em `127.0.0.1`, para o health check) e `caddy` (80/443, proxy reverso com WebSocket). A API confia em `X-Forwarded-For` porque `Proxy__TrustForwardedHeaders=true` está no `compose.yml`; isso só é seguro porque a porta da API não está exposta.
- Secrets do GitHub (environment `production`): `DEPLOY_SSH_KEY` (chave privada do usuário `deploy`), `DEPLOY_KNOWN_HOSTS`; variável `DEPLOY_HOST`. O pull da imagem usa o `GITHUB_TOKEN` do job, com login temporário no servidor.

Comandos úteis no servidor (como `deploy`, em `/srv/screencord`):

```bash
docker compose ps
docker compose logs -f api
curl -s http://127.0.0.1:8080/health
./deploy.sh 0.1.0   # deploy manual de uma versão já publicada no GHCR (exige docker login ghcr.io)
```

Restaurar um backup: `docker compose stop api`, apague `data/screencord.db-wal` e `data/screencord.db-shm`, copie o arquivo de `backups/` para `data/screencord.db` (como `deploy`) e `docker compose start api`.

## Quando houver domínio

1. Crie um registro A apontando para `154.16.50.20`.
2. No `deploy/Caddyfile`, troque `{$PUBLIC_IP}` pelo domínio e remova `default_sni` e o bloco `cert_issuer`.
3. Faça uma release (PATCH) e atualize o `Api:BaseUrl` do app.

## Cuidados

- O esquema do banco é criado por `DatabaseSchema.Ensure` (`EnsureCreated` + criação das tabelas e índices que faltam). Isso só cobre mudanças **aditivas**. Alterar ou remover colunas exige adotar EF Core Migrations antes; não faça isso sem planejar a migração dos dados de produção.
- A presença dos grupos fica em memória: a API roda com uma instância só. Todo deploy derruba as conexões SignalR e de mídia por alguns segundos; o app reconecta.
- O app recusa `http` fora de localhost; a URL de produção do app é `https://154.16.50.20`.
- Observabilidade (`Observability/`): métricas em memória, erros da API e do app na tabela `ErrorEvents`, usuários ativos por dia em `UserActivities` (`ActiveUsers`, uma linha por conta e dia, gravada na primeira requisição autenticada do dia) e o painel `/admin` (HTML em `dashboard.html`, recurso embutido). Os dados do painel vêm de relatórios anônimos: no HTML, tudo entra como texto (`textContent`), nunca como HTML. O `ErrorLogProvider` ignora as categorias do EF Core e de `Observability`, senão uma falha ao gravar vira um ciclo.

## Licença

- A API é AGPL-3.0 (`LICENSE`); o app é GPL-3.0. O nome e a marca ScreenCord ficam fora da licença.
- Toda dependência nova precisa ter licença compatível com a AGPL-3.0 (MIT, BSD, Apache-2.0 servem) e entrar no `THIRD-PARTY-NOTICES.txt`.
- Actions de terceiros (fora de `actions/*`) ficam fixadas pelo SHA do commit, com a versão num comentário.
