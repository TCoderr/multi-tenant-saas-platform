[English](README.md) · **Türkçe**

# Kulvar

Küçük ve orta ölçekli şirketlerin **müşteri kayıtlarını ve destek taleplerini** yönettiği çok kiracılı (multi-tenant) B2B SaaS uygulaması.
Her şirket kendi "kulvarında" çalışır: bir şirketin verisi, başka bir şirketin kullanıcısı tarafından ne okunabilir ne değiştirilebilir —
bu, uygulama kodunda, EF Core filtresinde **ve** PostgreSQL satır düzeyi güvenliğinde (RLS) ayrı ayrı zorlanır ve gerçek PostgreSQL üzerinde test edilir.

**Sahibi / yazar:** TCoderr

> Bu bir portföy projesidir. Gerçek ödeme, e-posta veya SMS göndermez; tüm demo verisi sentetiktir. Ödeme sağlayıcı ve posta kutusu **simülasyondur**.
> Bilinen sınırlar: [docs/known-limitations.md](docs/known-limitations.md).

## Ne yapar

- Kayıt, e-posta doğrulama, giriş/çıkış; sunucu tarafı oturum (cookie) + CSRF koruması; hesap kilitleme ve hız sınırı.
- Şirket (kiracı) oluşturma; roller: **Owner / Admin / Agent / Viewer**; davet (tek kullanımlık, süreli, doğrulanmış e-posta eşleşmeli);
  sahiplik devri (açık kabul ile, atomik); son Owner korunur.
- Müşteriler ve destek talepleri (Açık → İşlemde → Çözüldü → Kapalı, yeniden açma), düz metin yorumlar, denetim kaydı.
- Planlar ve haklar: Deneme (14 gün) / Starter / Team; koltuk, müşteri ve aylık talep kotaları **eşzamanlı isteklerde aşılmaz**.
- Simüle faturalama: plan değişikliği, imzalı (HMAC) webhook, yinelenen/sırasız/geç olaylara dayanıklı durum makinesi, mutabakat ve operatör çözümü.
- Dayanıklı outbox/inbox + kiralamalı (lease) worker; demo posta kutusu; CSV dışa aktarma (formül enjeksiyonuna karşı korumalı).
- Şirket silme: bekleme süresi → kalıcı temizleme → iz kaydı (tombstone).
- Platform operatörü paneli: **yalnız metadata**; şirket içeriğini okuyamaz, kimliğe bürünemez.

## Mimari (özet)

| Katman | Seçim |
| --- | --- |
| Arka uç | ASP.NET Core 10 (minimal API), modüler monolit: Identity, Tenancy, Customers, Support, Billing, Operations |
| Veri | PostgreSQL 18, EF Core 10; paylaşılan şema + `tenant_id` + **RLS (ENABLE + FORCE)**; bileşik yabancı anahtarlar |
| Veritabanı rolleri | `kulvar_owner` (yalnız migration), `kulvar_app` (API), `kulvar_platform` (operatör + webhook), `kulvar_worker` — hiçbiri superuser/BYPASSRLS değil |
| Kimlik | `__Host-` önekli HttpOnly cookie + sunucu tarafı oturum tablosu + antiforgery token (JWT yok; gerekçe: [ADR 0003](docs/adr/0003-auth-and-session.md)) |
| Arka plan işleri | Veritabanı outbox/inbox, kiralama + fencing token, kiracı başına adil dağıtım, yeniden deneme + ölü kuyruk |
| Arayüz | React 19 + TypeScript + Vite, aynı origin; TanStack Query (önbellek anahtarı kullanıcı + şirket kapsamlı) |
| Çalıştırma | Docker Compose, HTTPS, salt-okunur kök dosya sistemi, yetkisiz kullanıcı, Docker secret dosyaları |

Tasarım belgeleri: [docs/](docs/) (01–09) ve mimari kararlar: [docs/adr/](docs/adr/).
Kiracı izolasyonunun nasıl doğrulandığı: [docs/tenant-isolation-verification.md](docs/tenant-isolation-verification.md).

## Gereksinimler

- Docker Desktop (veya Docker Engine + Compose v2)
- .NET SDK 10.0.401 (`global.json`) — yerel build/test ve geliştirici HTTPS sertifikası için
- Node.js 24 + npm — yalnız arayüzü yerelde geliştirmek/test etmek için (Docker build kendi Node imajını kullanır)
- İlk kullanımda geliştirici sertifikası: `dotnet dev-certs https --check` (yoksa `dotnet dev-certs https --trust`)

Global araç kurulumu gerekmez. Yığın yalnız `127.0.0.1:8843` portunu açar; Compose proje adı `kulvar`'dır.

## Çalıştırma

### Windows (PowerShell)

```powershell
.\scripts\local-stack.ps1 init -SeedDemo          # sırlar, sertifika, demo parolaları (.local\ altında; ekrana yazılmaz)
.\scripts\local-stack.ps1 up -SeedDemo -Simulation # build + başlat + demo verisi
.\scripts\local-stack.ps1 status
.\scripts\local-stack.ps1 logs
.\scripts\local-stack.ps1 down                     # durdurur; veriler (volume) KALIR
```

### Linux / macOS / Git Bash

```bash
./scripts/local-stack.sh init --seed-demo
./scripts/local-stack.sh up --seed-demo --simulation
./scripts/local-stack.sh status
./scripts/local-stack.sh logs
./scripts/local-stack.sh down
```

Uygulama: **https://localhost:8843**

- `-Simulation` / `--simulation`: ödeme sonucunu (başarılı, reddedildi, zaman aşımı…) arayüzden seçmeyi ve sağlayıcı olayı tetiklemeyi açar. Yalnız `Development` ortamında kabul edilir.
- `-SeedDemo` olmadan başlatırsanız boş bir sistemle başlarsınız; kayıt olup doğrulama bağlantısını uygulama içindeki **demo posta kutusundan** alırsınız.
- Farklı port / yan yana ikinci yığın: `-Project <ad> -LocalDir <dizin> -Port <n>` (bash: `--project --local-dir --port`).

### Verileri silmek (yıkıcı, ayrı onay)

`down` hiçbir veriyi silmez. Veritabanı, anahtar ve export volume'larını silmek için açık onay gerekir; betik, volume'ların bu ürüne ait olduğunu
(etiket) doğrulamadan hiçbir şey silmez:

```powershell
.\scripts\local-stack.ps1 reset -ConfirmReset kulvar
```

```bash
./scripts/local-stack.sh reset --confirm-reset kulvar
```

## Demo hesapları

`init -SeedDemo` her hesap için rastgele bir parola üretir. **Parolalar bu belgede yoktur**; yerel, git'e girmeyen dosyalardadır:

```
.local/seed/Seed__Passwords__<anahtar>      # ör. .local/seed/Seed__Passwords__ayse.owner
```

| Anahtar | E-posta | Akasya Mobilya (A) | Boreas Yazılım (B) |
| --- | --- | --- | --- |
| `ayse.owner` | ayse.owner@akasya.test | Owner | — |
| `ali.admin` | ali.admin@akasya.test | Admin | — |
| `asli.agent` | asli.agent@akasya.test | Agent | — |
| `arda.viewer` | arda.viewer@akasya.test | Viewer | — |
| `burak.owner` | burak.owner@boreas.test | — | Owner |
| `belma.agent` | belma.agent@boreas.test | — | Agent |
| `cem.ortak` | cem.ortak@serbest.test | Agent | Admin |
| `operator` | operator@kulvar.test | — (platform operatörü) | — (platform operatörü) |

- **Akasya Mobilya (A):** Team planı, 5 üye, 6 müşteri, 7 talep.
- **Boreas Yazılım (B):** Deneme planı, 3 üye (koltuk limiti dolu), 3 müşteri, 4 talep.
- `cem.ortak` iki şirkette farklı rolle çalışır; şirket değiştirirken yetkilerin ve verinin değiştiğini göstermek içindir.
- `operator` hiçbir şirketin üyesi değildir; yalnız `/platform` panelini (metadata) görür.

Tüm kişiler, şirketler, e-postalar (`.test`) ve telefonlar uydurmadır.

## Geliştirme ve doğrulama

```powershell
.\scripts\verify.ps1 -Web      # kilitli restore, Release build, birim + entegrasyon testleri (gerçek PostgreSQL 18), web kapıları
.\scripts\stack-smoke.ps1      # izole Docker yığını (ayrı proje/port) üzerinde uçtan uca duman testi; bitince kendi kaynaklarını siler
.\scripts\e2e.ps1              # izole yığın üzerinde gerçek tarayıcı (Playwright + axe) testleri
.\scripts\scan-secrets.ps1     # sır/imza taraması + NuGet ve npm zafiyet denetimi
```

- Entegrasyon testleri kendi tek kullanımlık PostgreSQL kapsayıcısını (Testcontainers) başlatır ve **kısıtlı çalışma zamanı rolleriyle** koşar;
  geliştirme veritabanına dokunmaz. SQLite veya EF InMemory kullanılmaz.
- Doğrulama betikleri geliştirme yığınından (`kulvar`) ayrı Compose projeleri ve portlar kullanır.
- Kayıtlı doğrulama koşusunun sonuçları: [docs/final-verification.md](docs/final-verification.md); ham özetler: `artifacts/verification/`.
  Bunlar yerel sonuçlardır; depo push edilmeden önce, o belgede kayıtlı kaynak durumuna ve parmak izine aittir (belgedeki "uzak CI
  çalıştırılmadı" ifadesi o ana aittir). Sonraki değişiklikler: bu README İngilizceye çevrildi (`README.md` İngilizce, Türkçe metin
  `README.tr.md`); `84f5be4` ve `1ca1abb` commit'leri `scripts/local-stack.sh` dosyasını (çalıştırma izni; Linux'ta secret dosya izinleri) ve
  CI iş akışını değiştirdi. Bunlar nedeniyle (ve `scripts/source-fingerprint.ps1` `README.tr.md` dosyasını da saydığı için) güncel ağacın
  parmak izi kayıtlı değer değildir.
- Bu değişikliklerden sonraki yerel kontroller (2026-10-12; kayıtlı doğrulamanın parçası değildir, logları depoda tutulmaz): CI yığın adımı
  (`init`, boş veritabanına migration, demo seed) Linux dosya izinleriyle çalıştırıldı; bu yığına karşı tam tarayıcı E2E paketi koşuldu
  (21 / 21, kararsız 0, atlanan 0) ve `scripts/stack-smoke.ps1` koşuldu (33 / 33). Birim ve entegrasyon testleri yerelde yeniden
  koşulmadı.
- CI tanımı: [.github/workflows/ci.yml](.github/workflows/ci.yml). Uzak CI: `1ca1abb` commit'i için [koşu 38185570488](https://github.com/TCoderr/multi-tenant-saas-platform/actions/runs/38185570488)
  başarıyla tamamlandı (.NET birim + entegrasyon, web ve Docker yığını + tarayıcı uçtan uca işleri; 2026-10-12, UTC+3 tarihinde kontrol
  edildi). Bu ifade yalnız o commit'e bağlıdır: bu README metnini ekleyen commit dahil sonraki commit'ler için bir sonuç iddia edilmez.
  Güncel durum: [GitHub Actions](https://github.com/TCoderr/multi-tenant-saas-platform/actions).

Arayüzü yerelde geliştirmek için (`src/web`): `npm ci`, ardından `npm run dev` (API isteklerini çalışan yığına yönlendirir).

## Dizin yapısı

```
src/Kulvar.Api/                 bileşim kökü: API, worker, migrator, seed (tek imaj, farklı komutlar); EF migration'ları
src/Kulvar.Platform/            ortak altyapı: DbRunner (kapsamlı işlem), kiracı bağlamı, outbox, denetim, HTTP yardımcıları
src/Kulvar.Modules.*/           Identity, Tenancy, Customers, Support, Billing, Operations
src/web/                        React arayüzü
tests/Kulvar.UnitTests/         saf kural testleri
tests/Kulvar.IntegrationTests/  gerçek PostgreSQL üzerinde API, RLS, eşzamanlılık, migration testleri
db/bootstrap/                   veritabanı rolleri ve şemalar (küme düzeyi, bir kez)
scripts/                        yerel yığın, doğrulama, tarama betikleri
docs/                           gereksinimler, mimari, veri modeli, API, tehdit modeli, test planı, ADR'ler, raporlar
design/preview.html             arayüzün statik önizlemesi (Demo)
```

## Üçüncü taraf bileşenler

Bağımlılıklar ve lisans atıfları: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Kullanılan beceri/kaynak notları: [docs/skills-and-sources.md](docs/skills-and-sources.md).
