FROM mcr.microsoft.com/dotnet/sdk:latest AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app

COPY *.cs* ./

RUN dotnet publish -c Release


FROM mcr.microsoft.com/dotnet/runtime:latest AS runtime
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app

COPY --from=build /app/bin/Release/*/publish /app/

ENTRYPOINT ["./getsinks"]
