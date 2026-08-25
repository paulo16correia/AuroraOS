# Aurora — container image (RFC 12).
#
# Two stages so the runtime carries no SDK and no source. The runtime user is unprivileged and owns
# nothing but its data directory: the process that holds the audit key, the vault key and the
# operator's memory should not also be able to rewrite its own binary.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests alone, so a source-only change does not re-resolve every package.
# Central package management means the versions are pinned in one file and this layer proves it.
COPY Directory.Packages.props Directory.Build.props* Aurora.slnx ./
COPY src/Aurora.Core/Aurora.Core.csproj             src/Aurora.Core/
COPY src/Aurora.Adapters/Aurora.Adapters.csproj     src/Aurora.Adapters/
COPY src/Aurora.Server/Aurora.Server.csproj         src/Aurora.Server/
COPY src/Aurora.Core/packages.lock.json             src/Aurora.Core/
COPY src/Aurora.Adapters/packages.lock.json         src/Aurora.Adapters/
COPY src/Aurora.Server/packages.lock.json           src/Aurora.Server/

# Locked mode: the build fails if a resolved version differs from the lock file, so an image cannot
# quietly pick up a package nobody chose.
RUN dotnet restore src/Aurora.Server/Aurora.Server.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/Aurora.Server/Aurora.Server.csproj \
        -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# A fixed uid, so a volume written by one release is readable by the next.
RUN groupadd --gid 10001 aurora \
 && useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin aurora \
 && mkdir -p /var/lib/aurora && chown aurora:aurora /var/lib/aurora && chmod 700 /var/lib/aurora

WORKDIR /app
COPY --from=build --chown=root:root --chmod=555 /app ./

USER aurora:aurora

# One volume, and everything durable lives in it: the database, the keys, the anchor, the sandbox.
# A backup that copies this directory copies the instance.
VOLUME ["/var/lib/aurora"]

ENV Aurora__Port=8080 \
    Aurora__BindAddress=0.0.0.0 \
    Aurora__DbPath=/var/lib/aurora/aurora.db \
    Aurora__AuditKeyPath=/var/lib/aurora/audit.key \
    Aurora__AuditAnchorPath=/var/lib/aurora/audit.anchor \
    Aurora__SnapshotKeyPath=/var/lib/aurora/snapshot.key \
    Aurora__GenomeKeyPath=/var/lib/aurora/genome.key \
    Aurora__VaultKeyPath=/var/lib/aurora/vault.key \
    Aurora__PassphrasePath=/var/lib/aurora/passphrase.json \
    Aurora__SandboxRoot=/var/lib/aurora/sandbox \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# Liveness only. Readiness carries detail and lives behind the auth guard, which a container
# runtime has no business holding.
#
# Run through Aurora itself: this image has no curl and no wget, and its /bin/sh is dash, which has
# no /dev/tcp. Adding a package so a probe can run would be enlarging the attack surface to answer
# one question that Aurora can already answer.
HEALTHCHECK --interval=30s --timeout=10s --start-period=25s --retries=3 \
    CMD ["dotnet", "/app/Aurora.Server.dll", "health"]

ENTRYPOINT ["dotnet", "/app/Aurora.Server.dll"]
