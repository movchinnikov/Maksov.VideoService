# syntax=docker/dockerfile:1.7
# Build context is the repository root. Shared libraries (libs/) are built into NuGet packages in the first stage,
# and the service gets them from the local artifacts/nuget feed (same as in local development: make pack).
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS libs
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY .editorconfig ./
COPY build/ build/
COPY libs/ libs/
# Package version = hash of the libs sources: if the libraries change, the version changes, so the NuGet cache never serves a stale one.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    HASH=$(find build libs -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) -not -path '*/bin/*' -not -path '*/obj/*' | LC_ALL=C sort | xargs sha256sum | sha256sum | cut -c1-8) \
    && for p in Primitives BuildingBlocks Web; do \
         dotnet pack libs/Maksov.VideoService.$p -c Release -o /feed -p:VersionSuffix=local.$HASH || exit 1; \
       done

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY .editorconfig ./
COPY build/ build/
COPY --from=libs /feed artifacts/nuget/
COPY services/videos/ services/videos/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish services/videos/Maksov.VideoService.Videos.WebApi/Maksov.VideoService.Videos.WebApi.csproj -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=4s --start-period=20s --retries=5 CMD ["dotnet", "Maksov.VideoService.Videos.WebApi.dll", "--health"]
ENTRYPOINT ["dotnet", "Maksov.VideoService.Videos.WebApi.dll"]
