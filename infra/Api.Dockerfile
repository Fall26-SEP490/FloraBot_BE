FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/FloraBot.Api/FloraBot.Api.csproj src/FloraBot.Api/
RUN dotnet restore src/FloraBot.Api
COPY src/FloraBot.Api src/FloraBot.Api
RUN dotnet publish src/FloraBot.Api -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir -p /var/lib/florabot/keys && chown -R $APP_UID /var/lib/florabot
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FloraBot.Api.dll"]
