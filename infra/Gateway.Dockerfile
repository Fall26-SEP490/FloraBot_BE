FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/FloraBot.Gateway/FloraBot.Gateway.csproj src/FloraBot.Gateway/
RUN dotnet restore src/FloraBot.Gateway
COPY src/FloraBot.Gateway src/FloraBot.Gateway
RUN dotnet publish src/FloraBot.Gateway -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FloraBot.Gateway.dll"]
