FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /app
COPY Runner.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app
COPY --from=build /app/out .

RUN apt-get update && apt-get install -y dotnet-sdk-8.0

WORKDIR /runner
VOLUME /runner

ENTRYPOINT ["dotnet", "/app/Runner.dll"]