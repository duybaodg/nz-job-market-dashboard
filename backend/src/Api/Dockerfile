FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY NzJobMarketDashboard.sln ./
COPY backend/src/Domain/Domain.csproj backend/src/Domain/
COPY backend/src/Application/Application.csproj backend/src/Application/
COPY backend/src/Infrastructure/Infrastructure.csproj backend/src/Infrastructure/
COPY backend/src/Api/Api.csproj backend/src/Api/
RUN dotnet restore backend/src/Api/Api.csproj
COPY backend/src backend/src
RUN dotnet publish backend/src/Api/Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
COPY crawler ./crawler
RUN python3 -m pip install --break-system-packages --no-cache-dir -e ./crawler \
    && python3 -m pip install --break-system-packages --no-cache-dir msgspec patchright \
    && python3 -m playwright install chromium \
    && python3 -m playwright install-deps chromium
ENV SCRAPLING_FETCHER_TYPE=dynamic
ENTRYPOINT ["dotnet", "Api.dll"]
