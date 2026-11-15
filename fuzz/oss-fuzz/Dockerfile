# OSS-Fuzz builder image for EasyImageSharp. Prepared, not submitted; see fuzz/README.md ("OSS-Fuzz").
FROM gcr.io/oss-fuzz-base/base-builder

# The .NET SDK, from Microsoft's official install script. Invariant globalization keeps the SDK (and the
# self-contained harness, which also sets InvariantGlobalization) independent of the image's ICU version.
ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl python3 \
    && curl -sSfL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT" \
    && ln -s "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh

# SharpFuzz's assembly rewriter (MIT), pinned to the version the harness references.
RUN dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path /usr/local/bin

# libfuzzer-dotnet (MIT): the libFuzzer side of the bridge. Its source is compiled by build.sh with OSS-Fuzz's
# compiler and fuzzing engine, so the prebuilt release binaries are not used here. Pinned to the
# v2025.05.02.0904 release tag (commit bd39d4e88d715ab460a929943645be2a186cde52).
RUN git clone --depth 1 --branch v2025.05.02.0904 https://github.com/Metalnem/libfuzzer-dotnet $SRC/libfuzzer-dotnet

RUN git clone --depth 1 https://github.com/FarhanLodi/EasyImageSharp $SRC/easyimagesharp
WORKDIR $SRC/easyimagesharp
COPY build.sh $SRC/
