#!/usr/bin/env bash
#
# Aurora — backup (RFC 12 rule 3).
#
# Writes an encrypted-at-rest copy of the instance and proves it restores. A backup nobody has
# restored is a belief about a file, so this does both and reports the second one separately.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
label="${1:-$(date -u +%Y%m%dT%H%M%SZ)}"
dest="${AURORA_BACKUP_DIR:-${here}/backups}/${label}"

mkdir -p "$dest"

echo "[backup] writing to ${dest}"
docker compose exec -T aurora dotnet /app/Aurora.Server.dll backup /var/lib/aurora/backup-staging

# Out of the container and into the host, so a lost container is not a lost backup.
docker compose cp aurora:/var/lib/aurora/backup-staging/. "$dest/"
docker compose exec -T aurora sh -c 'rm -rf /var/lib/aurora/backup-staging'

newest="$(ls -1t "$dest"/aurora-*.db | head -1)"
echo "[backup] proving it restores"
docker compose cp "$newest" aurora:/tmp/verify.db
docker compose exec -T aurora dotnet /app/Aurora.Server.dll restore-test /tmp/verify.db
docker compose exec -T aurora sh -c 'rm -f /tmp/verify.db'

# Rule 3 asks for the configuration and the schema reference alongside the data. Without them a
# restore is a database nobody knows how to run.
cp "${here}/compose.yaml" "${dest}/" 2>/dev/null || true
cp "${here}/ops/Caddyfile" "${dest}/" 2>/dev/null || true
ls -1 "${here}/ops/releases"/*.json 2>/dev/null | tail -1 | xargs -I{} cp {} "${dest}/release.json" || true

echo "[backup] ${dest}"
echo "[backup] Keys are NOT in here. Store the contents of the volume's *.key files separately;"
echo "[backup] a backup that carries its own signing key proves nothing about itself."
