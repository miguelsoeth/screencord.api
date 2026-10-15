#!/usr/bin/env bash
# Sobe uma versão da API no VPS. Uso: deploy.sh 0.2.0
# Roda em /srv/screencord, chamado pelo workflow de deploy (ou à mão, como usuário deploy).
# Faz backup do banco, troca a imagem, espera o /health responder com a versão nova e,
# se não responder, volta para a versão anterior.
set -euo pipefail

VERSION="${1:?informe a versão, ex.: deploy.sh 0.2.0}"
VERSION="${VERSION#v}"
cd /srv/screencord

current_version() { grep -oP '^API_VERSION=\K.*' .env || true; }

set_version() {
    if grep -q '^API_VERSION=' .env; then
        sed -i "s/^API_VERSION=.*/API_VERSION=$1/" .env
    else
        echo "API_VERSION=$1" >> .env
    fi
}

wait_healthy() {
    for _ in $(seq 1 30); do
        if curl -fsS http://127.0.0.1:8080/health 2>/dev/null | grep -q "\"version\":\"$1\""; then
            return 0
        fi
        sleep 2
    done
    return 1
}

PREVIOUS="$(current_version)"
echo "Versão atual: ${PREVIOUS:-nenhuma}. Nova: $VERSION"

if [ -f data/screencord.db ]; then
    backup="backups/screencord-$(date +%Y%m%d-%H%M%S)-antes-de-v$VERSION.db"
    sqlite3 data/screencord.db ".backup '$backup'"
    echo "Backup: $backup"
fi

set_version "$VERSION"
docker compose pull api
docker compose up -d

if wait_healthy "$VERSION"; then
    echo "v$VERSION no ar."
    docker image prune -f > /dev/null
    exit 0
fi

echo "A v$VERSION não respondeu no /health. Últimos logs:" >&2
docker compose logs --tail 80 api >&2
if [ -n "$PREVIOUS" ] && [ "$PREVIOUS" != "$VERSION" ]; then
    echo "Voltando para a v$PREVIOUS." >&2
    set_version "$PREVIOUS"
    docker compose up -d
    wait_healthy "$PREVIOUS" && echo "v$PREVIOUS restaurada." >&2
fi
exit 1
