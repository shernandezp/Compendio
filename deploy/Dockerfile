# syntax=docker/dockerfile:1.7
#
# One service, non-root, shell-less.
#
# The chiselled base is the reason there is no third-party crypto and no native SQLite build in the
# dependency list: both would have needed a fatter image, and "one container, no sidecars" is a
# product requirement rather than a preference.
#
# BUILD CONTEXT: the *parent* of the Compendio checkout, so the COPY paths are Compendio-prefixed:
#
#   docker build -f Compendio/deploy/Dockerfile -t compendio ..
#
# Multi-arch:
#   docker buildx build --platform linux/amd64,linux/arm64 -f Compendio/deploy/Dockerfile -t compendio ..

# ---- Client ------------------------------------------------------------------------------------
FROM node:24-alpine AS client
WORKDIR /src/client

COPY Compendio/src/client/package.json Compendio/src/client/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY Compendio/src/client/ ./
RUN npm run check:i18n && npm run build


# ---- Server ------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src

COPY Compendio/Directory.Build.props Compendio/Directory.Packages.props Compendio/global.json Compendio/
COPY Compendio/src/Server/Compendio.Server.csproj Compendio/src/Server/
COPY Compendio/src/Server/Client.targets Compendio/src/Server/

RUN dotnet restore Compendio/src/Server/Compendio.Server.csproj

COPY Compendio/src/Server/ Compendio/src/Server/
COPY --from=client /src/Server/wwwroot/ Compendio/src/Server/wwwroot/

# The SPA is already built above; rebuilding it here would need Node in the SDK image too.
RUN dotnet publish Compendio/src/Server/Compendio.Server.csproj \
        -c Release \
        -o /app \
        -p:SkipClientBuild=true \
        --no-restore

# Pre-create the data tree here (the chiselled runtime has no shell to mkdir at runtime), so it can
# be copied in with the non-root user's ownership below.
RUN mkdir -p /data/content /data/db /data/keys /data/logs /data/backups


# ---- Runtime -----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_ENVIRONMENT=Production \
    Urls=http://0.0.0.0:8080 \
    DataDir=/data

WORKDIR /app
COPY --from=server /app .

# The runtime user is non-root (UID 64198) and cannot create directories under a root-owned /data,
# so the data tree is copied in already owned by it. Without this the first write to /data/logs
# fails with "Access to the path '/data/logs' is denied."
COPY --from=server --chown=64198:64198 /data /data

# No VOLUME declarations: under the containerd image store an anonymous volume is created root-owned
# rather than inheriting the 64198 ownership above, which stops the non-root process writing to
# /data/db or /data/keys. Persistence is always mounted explicitly instead — the three paths to
# mount (content, db, and keys separately) are documented in deploy/docker-compose.yml, because
# losing `keys` loses every encrypted page.

EXPOSE 8080

# The chiselled image has no shell, so the health check is the app checking itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/app/compendio", "--version"]

# Non-root by default in the chiselled base (UID 64198).
ENTRYPOINT ["/app/compendio"]
