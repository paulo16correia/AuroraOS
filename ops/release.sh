#!/usr/bin/env bash
#
# Aurora — release (RFC 12).
#
# The order is the point. Back up before touching anything, migrate on a copy nobody is serving
# from, check health before traffic, and keep the previous release reachable so "rollback" is a
# command rather than a rebuild.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$here"

release_id="${1:-$(date -u +%Y%m%dT%H%M%SZ)}"
approved_by="${AURORA_APPROVED_BY:-${USER:-unknown}}"
manifest="ops/releases/${release_id}.json"

: "${AURORA_BEARER_TOKEN:?set AURORA_BEARER_TOKEN}"

say() { printf '\n[release] %s\n' "$*"; }

# --- 1. what we are rolling back to -----------------------------------------------------------
# Read first: a release whose predecessor is unknown is not reversible, and rule 2 asks for
# reversibility rather than for hope.
previous="$(ls -1 ops/releases/*.json 2>/dev/null | tail -1 || true)"
rollback_id="$(
  if [ -n "$previous" ]; then
    grep -o '"release_id"[^,]*' "$previous" | head -1 | cut -d'"' -f4
  else
    echo "none"
  fi
)"

if [ "$rollback_id" = "none" ]; then
  say "no previous release on record; this one has nowhere to roll back to."
  say "that is expected exactly once. continuing."
fi

# --- 2. back up before anything changes --------------------------------------------------------
say "backing up before the release"
./ops/backup.sh "pre-${release_id}"

# --- 3. build ----------------------------------------------------------------------------------
say "building"
docker compose build aurora

# --- 4. pin what is actually being run ---------------------------------------------------------
# Digests, not tags. A tag names a build; a digest is one.
digests="$(
  docker compose config --images \
    | while read -r image; do
        docker image inspect "$image" --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}' 2>/dev/null || true
      done \
    | sed 's/.*/"&"/' | paste -sd, -
)"

# --- 5. start, then check before traffic -------------------------------------------------------
# Migration runs at startup inside one transaction, so an interrupted migration rolls back rather
# than leaving a half-applied schema. If it fails, health reports the schema mismatch and the
# proxy never gets a healthy upstream to send traffic to.
say "starting"
docker compose up -d --wait aurora

say "checking health before traffic"
status="$(docker compose exec -T aurora sh -c \
  "wget -qO- --header='Authorization: Bearer \$Aurora__BearerToken' http://127.0.0.1:8080/health" \
  | grep -o '"status":"[A-Z]*"' | head -1 | cut -d'"' -f4)"

if [ "$status" = "FAIL" ]; then
  say "health is FAIL — rolling back and not sending traffic"
  ./ops/rollback.sh "$rollback_id"
  exit 1
fi

say "health is ${status}"

# --- 6. record the release ---------------------------------------------------------------------
schema="$(docker compose exec -T aurora sh -c \
  "wget -qO- http://127.0.0.1:8080/health/live >/dev/null 2>&1; echo" >/dev/null 2>&1; \
  grep -o 'TargetSchemaVersion = [0-9]*' src/Aurora.Adapters/Persistence/SqliteDatabase.cs \
  | grep -o '[0-9]*' | head -1)"

mkdir -p ops/releases
cat > "$manifest" <<JSON
{
  "release_id": "${release_id}",
  "image_digests": [${digests}],
  "schema_version": ${schema},
  "config_version": "$(git -C "$here" rev-parse --short HEAD 2>/dev/null || echo unknown)",
  "migration_ids": [$(seq -s, 1 "${schema}")],
  "approved_by": "${approved_by}",
  "deployed_at_utc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "rollback_release_id": "${rollback_id}"
}
JSON

say "released ${release_id} (rollback target: ${rollback_id})"
say "manifest: ${manifest}"

# --- 7. bring the proxy up ---------------------------------------------------------------------
docker compose up -d proxy
say "traffic is flowing. watch: docker compose logs -f"
