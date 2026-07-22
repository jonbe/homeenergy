# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first (cached unless the project file changes).
COPY HomeEnergy.csproj ./
RUN dotnet restore HomeEnergy.csproj
COPY . .
RUN dotnet publish HomeEnergy.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Build identity, surfaced in the UI so we can tell which image is running.
ARG BUILD_SHA=dev
ARG BUILD_TIME=
# Persistent data (SQLite DB, Tibber token, PR history) lives here; mount a
# volume at /data so it survives image updates.
ENV ASPNETCORE_URLS=http://+:8080 \
    HomeEnergy__DataDir=/data \
    BUILD_SHA=$BUILD_SHA \
    BUILD_TIME=$BUILD_TIME
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "HomeEnergy.dll"]
