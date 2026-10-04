#!/bin/sh
# Installs Aurora as a service under its own account. Run from a clone of the repository:
#
#     sudo deploy/linux/install.sh
#
# Re-running it updates the application and keeps the configuration, the token and every record.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run it as root: sudo $0" >&2
    exit 1
fi

REPO=$(cd "$(dirname "$0")/../.." && pwd)
PREFIX=/opt/aurora
STATE=/var/lib/aurora
CONF=/etc/aurora
PORT=${AURORA_PORT:-5099}

DOTNET=$(command -v dotnet) || { echo "dotnet was not found on PATH; install the .NET 10 SDK first." >&2; exit 1; }

if ! command -v bwrap >/dev/null 2>&1; then
    echo "note: bubblewrap is not installed, so plugins will be refused (sudo apt install bubblewrap)."
fi

# The account Aurora runs as: no login, no shell, and its home is its state directory.
if ! id aurora >/dev/null 2>&1; then
    useradd --system --home-dir "$STATE" --shell /usr/sbin/nologin aurora
fi

# The application and the plugins, owned by root and read-only to Aurora: nothing it runs can be
# changed by the account that runs it.
install -d -m 0755 "$PREFIX"
"$DOTNET" publish "$REPO/src/Aurora.Server" -c Release -o "$PREFIX/app" --nologo -v quiet
rm -rf "${PREFIX:?}/plugins"
cp -R "$REPO/plugins" "$PREFIX/plugins"
find "$PREFIX/plugins" -name __pycache__ -type d -prune -exec rm -rf {} +
chown -R root:root "$PREFIX"
chmod -R u=rwX,go=rX "$PREFIX"

# Aurora's own state: the database, the key files, the passphrase verifier. Its account alone.
install -d -o aurora -g aurora -m 0700 "$STATE"

install -d -m 0750 -o root -g aurora "$CONF"
TOKEN=""
if [ ! -f "$CONF/aurora.env" ]; then
    TOKEN=$(od -An -tx1 -N32 /dev/urandom | tr -d ' \n')
    (
        umask 027
        cat > "$CONF/aurora.env" <<CONFIG
Aurora__Port=$PORT
Aurora__BearerToken=$TOKEN
Aurora__DbPath=$STATE/aurora.db
Aurora__SandboxRoot=$STATE/sandbox
Aurora__PluginRoot=$STATE/plugins
CONFIG
    )
    chown root:aurora "$CONF/aurora.env"
    chmod 0640 "$CONF/aurora.env"
fi

sed "s|@DOTNET@|$DOTNET|" "$REPO/deploy/linux/aurora.service" > /etc/systemd/system/aurora.service
sed "s|@DOTNET@|$DOTNET|" "$REPO/deploy/linux/aurora-admin" > /usr/local/bin/aurora-admin
chmod 0755 /usr/local/bin/aurora-admin

if [ -d /run/systemd/system ]; then
    systemctl daemon-reload
    systemctl enable --now aurora
    systemctl restart aurora
    STARTED="Aurora is running as a service: systemctl status aurora"
else
    STARTED="systemd is not running on this machine, so nothing was started; the unit is installed."
fi

cat <<SUMMARY

$STARTED

Next:
  1. Enrol the operator passphrase — it opens the panel and approves requests:
         sudo aurora-admin enroll-passphrase
  2. Open the control panel and sign in with it:
         http://127.0.0.1:$PORT/ui/login
  3. Point your MCP client at http://127.0.0.1:$PORT/mcp with the bearer token
     ($( [ -n "$TOKEN" ] && echo "printed below, once" || echo "kept in $CONF/aurora.env, readable with sudo" )).
  4. Check the machine, and install plugins from $PREFIX/plugins:
         sudo aurora-admin doctor
         sudo aurora-admin plugin install $PREFIX/plugins/discord
SUMMARY

if [ -n "$TOKEN" ]; then
    echo
    echo "Bearer token: $TOKEN"
fi
