# Kullanılan skill'ler ve sürüm kaynakları

## Skill'ler

| Skill | Kaynak | Lisans | Kullanım |
|---|---|---|---|
| `frontend-design` | Yerel resmi eklenti önbelleği: `~/.claude/plugins/marketplaces/claude-plugins-official/plugins/frontend-design/skills/frontend-design/SKILL.md` | Apache-2.0 | Yalnız okundu; tasarım planı süreci (renk/tip/yerleşim/ilke → varsayılanlara karşı gözden geçirme) [07-ui-design.md](07-ui-design.md) içinde uygulandı. Projeye dosya kopyalanmadı, betik çalıştırılmadı, global kurulum yapılmadı. |

Erişilebilirlik için ayrı bir skill bulunmadı (oturumdaki skill listesi ve yerel eklenti önbelleği tarandı). Uydurma skill adı kullanılmadı;
hedefler WCAG 2.2 AA'ya göre elle yazıldı.

## Araç ve sürüm doğrulaması (2026-10-10)

| Bileşen | Sürüm | Doğrulama kaynağı |
|---|---|---|
| .NET SDK | 10.0.401 (LTS) | Yerel `dotnet --list-sdks`; `global.json` ile sabit |
| ASP.NET Core / EF Core | 10.0.12 | `api.nuget.org/v3-flatcontainer/<paket>/index.json` (en yeni kararlı) |
| Npgsql / Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | nuget.org (aynı uç) |
| EFCore.NamingConventions | 10.0.1 | nuget.org |
| xunit.v3 | 4.0.2 | nuget.org |
| Testcontainers.PostgreSql | 4.16.0 | nuget.org |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | nuget.org |
| PostgreSQL | 18.6 (`postgres:18.6` resmi image) | Yerel image; Docker Hub resmi image |
| Node.js / npm | 24.18.0 / 12.1.0 | Yerel |
| React / React DOM | 19.3.0 | `npm view` (registry.npmjs.org) |
| Vite / @vitejs/plugin-react | 8.3.4 / 6.1.2 | `npm view` |
| TypeScript | 6.0.3 | `npm view`; 7.x yerine 6.0.x seçildi çünkü `typescript-eslint@8.71.1` eş bağımlılığı `<6.1.0` |
| TanStack Query / React Router | 5.104.1 / 7.18.4 | `npm view` |
| Vitest / Playwright / axe | 5.0.3 / 1.64.0 / 4.13.0 | `npm view`; Playwright kurulu Chrome kanalını kullanır (tarayıcı indirilmez) |
| Docker Engine / Compose | 29.8.2 / v5.5.1 | Yerel `docker --version` |

Kilit dosyaları: her .NET projesinde `packages.lock.json` (`RestorePackagesWithLockFile`), web'de `package-lock.json`.

## Örnek alınan teknik desenler

Komşu lojistik projesinin (yalnız okundu, değiştirilmedi) yerel yığın kalıbı örnek alındı: secret dosyaları `.local/` altında üretme,
`dotnet dev-certs` sertifikasını PFX olarak dışa aktarma, tek image + ayrı migrator servisi. Kod paylaşılmadı; ortak altyapı projesi açılmadı.
