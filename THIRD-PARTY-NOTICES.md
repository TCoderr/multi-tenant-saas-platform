# Üçüncü Taraf Bileşenler ve Atıflar

Kulvar'ın kendi kaynak kodunun sahibi **TCoderr**'dır. Aşağıdaki üçüncü taraf bileşenler kendi lisansları altındadır; bu depoya kaynak kodları
kopyalanmamıştır, paket yöneticileriyle (NuGet, npm) ve kapsayıcı imajlarıyla alınırlar. Lisans bilgisi her paketin kendi meta verisinden okunmuştur
(`*.nuspec` / `package.json`); geçişli bağımlılıkların tam listesi kilit dosyalarındadır (`packages.lock.json`, `src/web/package-lock.json`).

## Uygulamayla birlikte dağıtılanlar (çalışma zamanı)

| Bileşen | Sürüm | Lisans | Not |
|---|---|---|---|
| .NET / ASP.NET Core çalışma zamanı | 10.0 | MIT | © .NET Foundation and Contributors |
| Microsoft.EntityFrameworkCore (+ Relational) | 10.0.12 | MIT | © Microsoft |
| Npgsql | 10.0.3 | PostgreSQL License | © The Npgsql Development Team |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL License | © The Npgsql Development Team |
| EFCore.NamingConventions | 10.0.1 | Apache-2.0 | © Shay Rojansky |
| React, React DOM | 19.3.0 | MIT | © Meta Platforms, Inc. and affiliates |
| React Router (`react-router-dom`) | 7.18.4 | MIT | © Remix Software Inc. ve katkıda bulunanlar |
| TanStack Query (`@tanstack/react-query`) | 5.104.1 | MIT | © Tanner Linsley |
| IBM Plex Sans, IBM Plex Sans Condensed (yazı tipleri; `@fontsource/*` paketleriyle) | 5.3.0 | SIL Open Font License 1.1 | Copyright IBM Corp. (telif satırları lisans dosyalarında aynen korunur). Yazı tipi dosyaları değiştirilmeden, uygulama paketine gömülü olarak dağıtılır; lisans metinleri uygulamayla birlikte `/licenses/IBM-Plex-Sans-OFL-1.1.txt` ve `/licenses/IBM-Plex-Sans-Condensed-OFL-1.1.txt` yollarında sunulur (kaynak: `src/web/public/licenses/`). |

## Yalnız geliştirme, test ve build sırasında kullanılanlar (dağıtılmaz)

| Bileşen | Sürüm | Lisans |
|---|---|---|
| Microsoft.EntityFrameworkCore.Design, `dotnet-ef` | 10.0.12 | MIT |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | MIT |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | MIT |
| xunit.v3 | 4.0.2 | Apache-2.0 |
| Testcontainers.PostgreSql | 4.16.0 | MIT |
| TypeScript | 6.0.3 | Apache-2.0 |
| Vite, `@vitejs/plugin-react` | 8.3.4, 6.1.2 | MIT |
| Vitest, jsdom | 5.0.3, 30.1.2 | MIT |
| Testing Library (`dom`, `react`, `jest-dom`, `user-event`) | 10.4.2, 16.3.3, 7.0.1, 14.6.7 | MIT |
| ESLint, `@eslint/js`, `typescript-eslint`, `eslint-plugin-react-hooks`, `globals` | 10.12.0, 10.0.1, 8.71.1, 7.1.1, 17.13.0 | MIT |
| Playwright (`@playwright/test`) | 1.64.0 | Apache-2.0 |
| axe-core (`@axe-core/playwright`) | 4.13.0 | MPL-2.0 |
| `@types/node`, `@types/react`, `@types/react-dom` | 24.19.2, 19.3.0, 19.3.0 | MIT |

## Kapsayıcı imajları

| İmaj | Kullanım | Lisans |
|---|---|---|
| `postgres:18.6` | Veritabanı (yerel yığın ve testler) | PostgreSQL License |
| `mcr.microsoft.com/dotnet/sdk:10.0.401`, `mcr.microsoft.com/dotnet/aspnet:10.0.12` | Build ve çalışma zamanı | MIT (.NET); taban işletim sistemi paketleri kendi lisanslarıyla |
| `node:24.18.0-bookworm-slim` | Yalnız arayüz build aşaması | MIT (Node.js); taban işletim sistemi paketleri kendi lisanslarıyla |

## Okunan kaynaklar

Tasarım sürecinde yalnız okunan (projeye dosyası kopyalanmayan) kaynaklar ve lisansları: [docs/skills-and-sources.md](docs/skills-and-sources.md).
