# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Switchboard.Web/Switchboard.Web.csproj -c Release -o /app/publish -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
RUN mkdir -p /data/files /data/keys && chown -R app:app /data
COPY --from=build /app/publish .
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD ["dotnet", "Switchboard.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Switchboard.Web.dll"]
