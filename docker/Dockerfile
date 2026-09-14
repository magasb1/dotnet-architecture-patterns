# Build from the repository root: docker build -f docker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props NuGet.config ./
COPY protos/ protos/
COPY src/StorageDemo.Core/ src/StorageDemo.Core/
COPY src/StorageDemo.Infrastructure/ src/StorageDemo.Infrastructure/
COPY src/StorageDemo.Api/ src/StorageDemo.Api/

RUN dotnet publish src/StorageDemo.Api/StorageDemo.Api.csproj -c Release -o /app

# FFmpeg ships in the image rather than coming from the distro, so nothing depends on the host's
# build. The NuGet package carries every platform it supports; only this one is worth keeping, and
# NuGet does not preserve the execute bit.
RUN find /app/ffmpeg -mindepth 1 -maxdepth 1 ! -name linux-x64 -exec rm -rf {} + \
    && chmod +x /app/ffmpeg/linux-x64/ffmpeg /app/ffmpeg/linux-x64/ffprobe

# That packaged build is LGPL and has no libsrt, so it is replaced by one that does, matching what
# scripts/fetch-ffmpeg.sh puts in place for local runs. Same version family the bindings expect:
# 8.1 carries the same library majors as 8.0. Downloaded here rather than committed, since it is
# 70 MB of binaries.
ARG FFMPEG_BUILD=ffmpeg-n8.1-latest-linux64-gpl-shared-8.1
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl xz-utils ca-certificates \
    && curl -fsSL -o /tmp/ffmpeg.tar.xz \
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${FFMPEG_BUILD}.tar.xz" \
    && tar -xJf /tmp/ffmpeg.tar.xz -C /tmp \
    && cp -P /tmp/${FFMPEG_BUILD}/lib/*.so* /app/ffmpeg/linux-x64/ \
    && cp /tmp/${FFMPEG_BUILD}/bin/ffmpeg /tmp/${FFMPEG_BUILD}/bin/ffprobe /app/ffmpeg/linux-x64/ \
    && chmod +x /app/ffmpeg/linux-x64/ffmpeg /app/ffmpeg/linux-x64/ffprobe \
    && rm -rf /tmp/ffmpeg.tar.xz /tmp/${FFMPEG_BUILD} /var/lib/apt/lists/*

# RF-DETR Nano, exported to ONNX inside the image rather than expected on the host. This mirrors
# scripts/fetch-rfdetr.sh exactly, minus the venv dance that script does for a shared host machine
# and a throwaway build stage does not need. CPU wheels are correct here even though the worker
# itself may run on a GPU: this stage exports one file and never runs inference at any real
# throughput, so there is nothing for a GPU to speed up.
#
# The version pins matter more than usual and are kept identical to the script's: the .NET runner
# is tested against the tensor names and shapes this exact exporter version produces, and rfdetr
# has changed both before.
FROM python:3.12-slim AS rfdetr-export
WORKDIR /export
RUN python -m pip install --no-cache-dir --quiet \
        --extra-index-url https://download.pytorch.org/whl/cpu \
        rfdetr==1.10.1 torch==2.14.0+cpu torchvision==0.29.0+cpu onnxruntime==1.30.0 onnx==1.22.0
COPY scripts/export-rfdetr.py scripts/export-rfdetr.py
RUN mkdir -p models && python scripts/export-rfdetr.py

# Shared by the API and the worker: the same FFmpeg, the same user, the same loader path. The
# worker is a plain host and needs none of ASP.NET, but one base is one set of natives to keep
# honest, and the difference in size is a few megabytes.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# The bundled FFmpeg links against these at load time, for hardware acceleration it will never use
# inside a container. They are small, and the alternative is building FFmpeg per deployment. Note
# that the image carries them: nothing here depends on what the host machine happens to have.
#
# libsrt is different: unlike FFmpeg it is a stable C ABI with a soname, so the distro build is as
# good as any and there is nothing to match versions with. Windows has no such package, which is
# why scripts/fetch-libsrt.sh copies a DLL next to the fetched FFmpeg there and this does not.
# The base is Ubuntu noble, which ships 1.5.3 under libsrt.so.1.5 -- new enough for the listen
# callback and the extended rejection codes.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libx11-6 libdrm2 libva2 libva-drm2 libvdpau1 libvpl2 \
        libsrt1.5-openssl \
    && rm -rf /var/lib/apt/lists/*

# Non-root. The image carries no credentials; everything comes from the environment at runtime.
RUN useradd --system --uid 64198 --create-home storagedemo \
    && mkdir -p /data \
    && chown -R storagedemo:storagedemo /data
USER storagedemo

# libav is loaded into this process by explicit path, but the libraries then resolve each other
# through the normal loader, which does not look next to the library it just loaded. Windows does;
# Linux needs telling. Without this, avformat cannot find avcodec and nothing decodes.
ENV LD_LIBRARY_PATH=/app/ffmpeg/linux-x64

# The detection worker (detection-plan.md D2), a separate deployment from the ingest pod:
#   docker build -f docker/Dockerfile --target worker -t storage-demo-worker .
# It carries the same libav and the ONNX Runtime natives the Infrastructure project references,
# and no ports: it subscribes to the API and posts back to it. RF-DETR Nano is baked in from the
# rfdetr-export stage above - 108 MB added to the image, in exchange for `docker build` alone being
# enough to run detection, nothing to fetch on the host first. On a GPU node this base becomes an
# NVIDIA CUDA runtime image and nothing else changes: the same natives carry the CUDA provider and
# the runner picks it when it loads (D3).
FROM build AS build-worker
COPY src/StorageDemo.Worker/ src/StorageDemo.Worker/
RUN dotnet publish src/StorageDemo.Worker/StorageDemo.Worker.csproj -c Release -p:OnnxRuntimeFlavor=Cuda -o /worker \
    && rm -rf /worker/ffmpeg \
    && cp -r /app/ffmpeg /worker/ffmpeg

FROM base AS worker
COPY --from=build-worker --chown=storagedemo:storagedemo /worker .
COPY --from=rfdetr-export --chown=storagedemo:storagedemo /export/models/rf-detr-nano.onnx models/rf-detr-nano.onnx
ENTRYPOINT ["dotnet", "StorageDemo.Worker.dll"]

# Last, so a build that names no target is still the API.
FROM base AS runtime
COPY --from=build --chown=storagedemo:storagedemo /app .

# Two cleartext ports, because HTTP/1.1 and HTTP/2 cannot share one without TLS to negotiate
# with: 8080 serves REST and the health probes, 5080 serves gRPC. TLS terminates at the ingress.
EXPOSE 8080
EXPOSE 5080

ENTRYPOINT ["dotnet", "StorageDemo.Api.dll"]
