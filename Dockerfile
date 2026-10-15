# Imagem de produção da API. O CI passa VERSION com a versão da tag (ex.: 0.2.0).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
WORKDIR /src

# Restaura antes de copiar o código, para o cache do Docker reaproveitar os pacotes.
COPY src/ScreenCord.Contracts/ScreenCord.Contracts.csproj src/ScreenCord.Contracts/
COPY src/ScreenCord.Api/ScreenCord.Api.csproj src/ScreenCord.Api/
RUN dotnet restore src/ScreenCord.Api/ScreenCord.Api.csproj

COPY src/ src/
RUN dotnet publish src/ScreenCord.Api/ScreenCord.Api.csproj -c Release -o /app --no-restore -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# O banco SQLite fica em /data, montado como volume. O usuário "app" (uid 1654) vem da imagem base.
RUN mkdir /data && chown app:app /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Default="Data Source=/data/screencord.db"
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScreenCord.Api.dll"]
