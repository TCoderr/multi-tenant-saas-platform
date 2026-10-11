# syntax=docker/dockerfile:1
# Kulvar: tek image, birden çok kullanım (API, --worker, --migrate, --seed, --probe).
# Secret, sertifika ve parola image katmanlarına girmez; çalışma zamanında salt-okunur dosya olarak bağlanır.

# ---- build: resmi .NET SDK (global.json ile aynı sürüm) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src

# Bağımlılık katmanı: yalnız proje/kilit dosyaları değişince restore yeniden çalışır.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Kulvar.Api/Kulvar.Api.csproj src/Kulvar.Api/packages.lock.json src/Kulvar.Api/
COPY src/Kulvar.Platform/Kulvar.Platform.csproj src/Kulvar.Platform/packages.lock.json src/Kulvar.Platform/
COPY src/Kulvar.Modules.Identity/Kulvar.Modules.Identity.csproj src/Kulvar.Modules.Identity/packages.lock.json src/Kulvar.Modules.Identity/
COPY src/Kulvar.Modules.Tenancy/Kulvar.Modules.Tenancy.csproj src/Kulvar.Modules.Tenancy/packages.lock.json src/Kulvar.Modules.Tenancy/
COPY src/Kulvar.Modules.Customers/Kulvar.Modules.Customers.csproj src/Kulvar.Modules.Customers/packages.lock.json src/Kulvar.Modules.Customers/
COPY src/Kulvar.Modules.Support/Kulvar.Modules.Support.csproj src/Kulvar.Modules.Support/packages.lock.json src/Kulvar.Modules.Support/
COPY src/Kulvar.Modules.Billing/Kulvar.Modules.Billing.csproj src/Kulvar.Modules.Billing/packages.lock.json src/Kulvar.Modules.Billing/
COPY src/Kulvar.Modules.Operations/Kulvar.Modules.Operations.csproj src/Kulvar.Modules.Operations/packages.lock.json src/Kulvar.Modules.Operations/
RUN dotnet restore src/Kulvar.Api/Kulvar.Api.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/Kulvar.Api/Kulvar.Api.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

# ---- web: React SPA statik çıktısı; resmi Node image'ı, kilit dosyasından kurulum ----
FROM node:24.18.0-bookworm-slim AS web
ENV CI=1 \
    NPM_CONFIG_UPDATE_NOTIFIER=false
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/web/ ./
RUN npm run build

# ---- runtime: resmi ASP.NET Core runtime, root olmayan kullanıcı ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app

# Yazılabilir yalnız iki dizin vardır (volume'lar buraya bağlanır); uygulama dosyaları root'a ait ve salt-okunurdur.
RUN mkdir -p /var/lib/kulvar/dataprotection /var/lib/kulvar/exports \
    && chown -R "$APP_UID" /var/lib/kulvar \
    && chmod 0700 /var/lib/kulvar/dataprotection /var/lib/kulvar/exports

COPY --from=build --chown=root:root /app/publish/ ./
# SPA aynı origin'den sunulur (wwwroot); kaynak haritası ve node_modules image'a girmez.
COPY --from=web --chown=root:root /web/dist/ ./wwwroot/

ENV ASPNETCORE_URLS=https://+:8843 \
    DataProtection__KeysPath=/var/lib/kulvar/dataprotection \
    Exports__Root=/var/lib/kulvar/exports \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

USER $APP_UID
EXPOSE 8843
ENTRYPOINT ["dotnet", "Kulvar.Api.dll"]
