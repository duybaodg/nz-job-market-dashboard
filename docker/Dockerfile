# syntax=docker/dockerfile:1

# Round 19: one build graph for the Api and Worker. The expensive
# python + pip + chromium + patchright + scrapling stack is its own
# `crawler-base` stage, so both runtime targets share the exact same layers
# (Docker stores them once) and a serial `docker compose build worker` /
# `docker compose build backend` reuses the cached stage.
#
# Layer order is deliberate:
#   1. apt python
#   2. crawler/pyproject.toml only -> pip installs dependencies
#   3. playwright/patchright chromium + OS dependencies
#   4. crawler source + `pip install --no-deps -e`
#   5. (runtime target) the .NET publish output
# A crawler-code change therefore only re-runs step 4 (seconds); a C#-only
# change only rebuilds its publish stage and the final COPY. Browser downloads
# happen only when the dependency pins in pyproject.toml change.
ARG DOTNET_RUNTIME=mcr.microsoft.com/dotnet/aspnet:9.0

FROM ${DOTNET_RUNTIME} AS crawler-base
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*

# Only the dependency manifest is in this layer. The temporary package stub
# lets `pip install -e` resolve the pinned dependency set without copying any
# crawler code; it is overwritten by the real source below.
COPY crawler/pyproject.toml ./crawler/pyproject.toml
RUN mkdir -p crawler/adapters \
    && : > crawler/adapters/__init__.py \
    && python3 -m pip install --break-system-packages --no-cache-dir -e ./crawler

# Browser/runtime layer: expensive, and independent of crawler source changes.
RUN python3 -m playwright install chromium \
    && patchright install chromium \
    && python3 -m playwright install-deps chromium \
    && python3 -c "import os; from playwright.sync_api import sync_playwright; p=sync_playwright().start(); x=p.chromium.executable_path; p.stop(); assert os.path.exists(x), x" \
    && python3 -c "import os; from patchright.sync_api import sync_playwright; p=sync_playwright().start(); x=p.chromium.executable_path; p.stop(); assert os.path.exists(x), x"

# Crawler source layer: the only layer a normal crawler edit invalidates.
COPY crawler ./crawler
RUN python3 -m pip install --break-system-packages --no-cache-dir --no-deps -e ./crawler
ENV SCRAPLING_FETCHER_TYPE=dynamic

# ---- .NET build stages -----------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-api
WORKDIR /src
COPY NzJobMarketDashboard.sln ./
COPY backend/src/Domain/Domain.csproj backend/src/Domain/
COPY backend/src/Application/Application.csproj backend/src/Application/
COPY backend/src/Infrastructure/Infrastructure.csproj backend/src/Infrastructure/
COPY backend/src/Api/Api.csproj backend/src/Api/
RUN dotnet restore backend/src/Api/Api.csproj
COPY backend/src backend/src
RUN dotnet publish backend/src/Api/Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-worker
WORKDIR /src
COPY NzJobMarketDashboard.sln ./
COPY backend/src/Domain/Domain.csproj backend/src/Domain/
COPY backend/src/Application/Application.csproj backend/src/Application/
COPY backend/src/Infrastructure/Infrastructure.csproj backend/src/Infrastructure/
COPY backend/src/Worker/Worker.csproj backend/src/Worker/
RUN dotnet restore backend/src/Worker/Worker.csproj
COPY backend/src backend/src
RUN dotnet publish backend/src/Worker/Worker.csproj -c Release -o /app/publish --no-restore

# ---- runtime targets -------------------------------------------------------
FROM crawler-base AS api
COPY --from=build-api /app/publish .
ENTRYPOINT ["dotnet", "Api.dll"]

FROM crawler-base AS worker
COPY --from=build-worker /app/publish .
ENTRYPOINT ["dotnet", "Worker.dll"]
