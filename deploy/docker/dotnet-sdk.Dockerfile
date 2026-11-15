# Image for make test / make lint / make pack: .NET SDK + ffmpeg (media worker integration tests).
FROM mcr.microsoft.com/dotnet/sdk:11.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_PACKAGES=/nuget
WORKDIR /src

