#!/usr/bin/env bash
#
# Aurora — restore (RFC 12 limit case: complete VPS failure).
#
# Deliberately noisy and deliberately manual. Restoring replaces the instance's memory, and the
# outstanding external calls it was in the middle of are reconciled afterwards, not assumed away.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$here"

backup="${1:?usage: restore.sh <path to a verified aurora-*.db>}"
[ -f "$backup" ] || { echo "[restore] no such backup: ${backup}" >&2; exit 1; }

manifest="${backup}.json"
if [ -f "$manifest" ] && grep -q '"RESTORE_TESTED"' "$manifest"; then
  echo "[restore] this backup has been restore-tested."
else
  echo "[restore] WARNING: this backup has not been restore-tested. It may not restore."
fi

cat <<'WARN'

This replaces everything Aurora knows with the contents of that file.

  - Memories, goals, missions and the audit journal are replaced wholesale.
  - Anything Aurora did between the backup and now is gone from its record but
    may still have happened in the world. Reconcile outstanding external calls
    afterwards; nothing here can do that for you.
  - The signing keys are NOT in the backup. Restore them from wherever they were
    kept, or the audit chain will not verify and Aurora will refuse to start.
    If they are gone for good: seal-audit-break, which repairs nothing.

WARN

read -r -p "Type RESTORE to continue: " confirm
[ "$confirm" = "RESTORE" ] || { echo "[restore] nothing done."; exit 1; }

docker compose stop proxy aurora
docker compose cp "$backup" aurora:/var/lib/aurora/aurora.db
[ -f "${backup}.anchor" ] && docker compose cp "${backup}.anchor" aurora:/var/lib/aurora/audit.anchor

docker compose up -d --wait aurora
echo "[restore] Aurora started, which means its audit chain and clock both passed."
echo "[restore] Check /health before letting traffic in:  docker compose up -d proxy"
