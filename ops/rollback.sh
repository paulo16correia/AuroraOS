#!/usr/bin/env bash
#
# Aurora — rollback (RFC 12).
#
# Goes back to the images a previous release recorded. It deliberately does not restore data: a
# schema migration that already committed is not undone by running an older binary, and pretending
# otherwise is how a rollback becomes the incident. If the schema moved, restore the pre-release
# backup with ops/restore.sh first, then run this.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$here"

release_id="${1:?usage: rollback.sh <release_id>}"
manifest="ops/releases/${release_id}.json"

if [ ! -f "$manifest" ]; then
  echo "[rollback] no manifest for ${release_id}. Nothing to go back to." >&2
  exit 1
fi

echo "[rollback] stopping the proxy so nothing new arrives"
docker compose stop proxy

echo "[rollback] returning to the images recorded in ${manifest}:"
grep -o '"image_digests".*\]' "$manifest"

echo
echo "[rollback] If this release changed the schema, restore first:"
echo "           ops/restore.sh <the pre-${release_id} backup>"
echo "           A committed migration is not undone by an older binary."
echo
read -r -p "Type ROLLBACK to bring the recorded images up: " confirm
[ "$confirm" = "ROLLBACK" ] || { echo "[rollback] nothing done."; exit 1; }

docker compose up -d --wait aurora
docker compose up -d proxy

echo "[rollback] back on ${release_id}."
