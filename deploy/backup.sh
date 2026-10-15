#!/usr/bin/env bash
# Backup diário do SQLite (cron do usuário deploy). Mantém os últimos 7 dias de backups diários
# e 10 backups feitos antes de deploys.
set -euo pipefail
cd /srv/screencord

[ -f data/screencord.db ] || exit 0
sqlite3 data/screencord.db ".backup 'backups/screencord-diario-$(date +%Y%m%d).db'"
find backups -name 'screencord-diario-*.db' -mtime +7 -delete
ls -1t backups/screencord-*-antes-de-*.db 2>/dev/null | tail -n +11 | xargs -r rm -f
