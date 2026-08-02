# The Runner targets net11.0 and uses the in-box System.IO.Compression ZStandard APIs.
#
# The newest tagged SDK image (sdk:11.0-preview) is still .NET 11 preview.6, which predates
# ZstandardCompressionOptions.WindowLog2 / ZstandardDecompressionOptions and therefore cannot build
# the Runner. Layer the current daily 11.0 SDK on top of it until .NET 11 ships, at which point this
# can collapse back to a plain 'FROM mcr.microsoft.com/dotnet/sdk:11.0'.
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS sdk
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && wget -qO- https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 11.0 --quality daily --install-dir /usr/local/dotnet \
    && rm -rf /var/lib/apt/lists/*
ENV DOTNET_ROOT=/usr/local/dotnet
ENV PATH="/usr/local/dotnet:${PATH}"

FROM sdk AS build

WORKDIR /app
COPY Runner.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o out

FROM sdk
WORKDIR /app
COPY --from=build /app/out .

COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

WORKDIR /runner
VOLUME /runner

ENTRYPOINT ["/app/entrypoint.sh"]