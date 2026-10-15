# Built from the repository root: docker build -f src/Tessera.Api/Dockerfile .
#
# Two stages. The build stage restores and publishes; the runtime stage carries
# only the output, so the image ships no SDK, no source and no NuGet cache.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, on their own layer: restore is the slow step and it only
# needs to re-run when a dependency changes, not when a line of code does.
COPY src/Tessera.Core/Tessera.Core.csproj      src/Tessera.Core/
COPY src/Tessera.Data/Tessera.Data.csproj      src/Tessera.Data/
COPY src/Tessera.Api/Tessera.Api.csproj        src/Tessera.Api/
RUN dotnet restore src/Tessera.Api/Tessera.Api.csproj

COPY src/ src/

RUN dotnet publish src/Tessera.Api/Tessera.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SkiaSharp needs a few native bits that the runtime image does not carry.
# fontconfig is required even when nothing draws text; without it Skia aborts
# on first use rather than degrading. libvips (with libheif) is the second
# thumbnail decoder: HEIC/HEIF from phones, AVIF, TIFF - none of which Skia
# reads - and the fallback for anything Skia refuses. Ubuntu ships libheif's
# codecs as plugins; without libde265 every HEVC-coded HEIC is "unsupported".
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 libfreetype6 wget \
      libvips-tools libheif-plugin-libde265 libheif-plugin-aomdec \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Never run as root: this container mounts a 900 GB library, and read-only
# mounts are a guarantee only if the process could not remount them anyway.
RUN useradd --system --uid 10001 tessera && chown -R tessera:tessera /app
USER tessera

ENV DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1 \
    ASPNETCORE_URLS=http://+:2500

EXPOSE 2500
ENTRYPOINT ["dotnet", "Tessera.Api.dll"]
