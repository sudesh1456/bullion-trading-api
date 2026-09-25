# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY BullionTrading.sln ./
COPY src/BullionTrading.Api/BullionTrading.Api.csproj src/BullionTrading.Api/
COPY tests/BullionTrading.Tests/BullionTrading.Tests.csproj tests/BullionTrading.Tests/
RUN dotnet restore
COPY . .
RUN dotnet publish src/BullionTrading.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/data/bullion.db"
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "BullionTrading.Api.dll"]
