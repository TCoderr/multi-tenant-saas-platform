# Final Doğrulama

**Kaynak parmak izi:** `b30967daa618d93e` (210 dosya; `scripts/source-fingerprint.ps1` — git'in gördüğü kaynak dosyaları; `docs/`, `artifacts/`, `design/` ve `README.md` hariç)
**Tarih:** 2026-10-10 21:19–21:24 UTC (yerel 2026-10-11 00:19–00:24) · **Ortam:** Windows 11, Docker Desktop 29.8.2 (WSL2), .NET SDK 10.0.401, Node.js 24, PostgreSQL 18.6, Chrome (kurulu kanal)

Aşağıdaki tüm komutlar **aynı parmak izi** üzerinde, art arda koşuldu; koşunun başında ve sonunda parmak izi aynıydı.
Bu belge yalnız yerelde gerçekten çalıştırılanları kaydeder.

## 1. Kapı sonuçları

| # | Kapı | Komut | Exit | Sonuç (toplam / geçen / başarısız / atlanan) | Süre | Log |
|---|---|---|---|---|---|---|
| 1 | Kilitli restore | `dotnet restore Kulvar.slnx --locked-mode` | 0 | — | 1 sn | `artifacts/verification/restore.log` |
| 1 | Release build (uyarılar hata; temiz, artımsız) | `dotnet build Kulvar.slnx -c Release --no-incremental` | 0 | 0 uyarı, 0 hata | — | `artifacts/verification/build.log` |
| 1 | Birim testleri | `dotnet test --project tests/Kulvar.UnitTests -c Release` | 0 | 148 / 148 / 0 / 0 | 4 sn | `artifacts/verification/unit-tests.log` |
| 1, 3, 5–8 | Entegrasyon testleri (Testcontainers PostgreSQL 18.6, kısıtlı çalışma zamanı rolleri) | `dotnet test --project tests/Kulvar.IntegrationTests -c Release` | 0 | 212 / 212 / 0 / 0 | 66 sn | `artifacts/verification/integration-tests.log` |
| 2 | Web: bağımlılık kurulumu (kilit dosyasından) | `npm ci` | 0 | — | 4 sn | `artifacts/verification/web-install.log` |
| 2 | Web: tip denetimi | `npm run typecheck` | 0 | hata yok | 4 sn | `artifacts/verification/web-typecheck.log` |
| 2 | Web: lint (uyarı eşiği 0) | `npm run lint` | 0 | 0 hata, 0 uyarı | 33 sn | `artifacts/verification/web-lint.log` |
| 2 | Web: bileşen testleri (Vitest) | `npm test` | 0 | 37 / 37 / 0 / 0 (5 dosya) | 37 sn | `artifacts/verification/web-tests.log` |
| 2 | Web: üretim build | `npm run build` | 0 | — | 4 sn | `artifacts/verification/web-build.log` |
| 9, 12 | Docker yığını duman testi (HTTPS, izole proje `kulvar-smoke`, port 8853) | `scripts/stack-smoke.ps1` | 0 | 33 / 33 / 0 / 0 | 141 sn | `artifacts/verification/stack-smoke.log` |
| 4, 5, 10 | Tarayıcı uçtan uca (Playwright + axe, izole proje `kulvar-e2e`, port 8863) | `scripts/e2e.ps1` | 0 | 21 / 21 / 0 / 0 (kararsız 0) | 104 sn | `artifacts/verification/e2e.log` |
| 11 | Depo taraması (sır, yasak dosya, e-posta alan adı, yazarlık, git durumu, zafiyet) | `scripts/scan-secrets.ps1` | 0 | 0 bulgu (311 dosya tarandı) | — | `artifacts/verification/scan-summary.log`, `scan-nuget-vulnerable.log`, `scan-npm-audit.log` |

Toplu özet (betiğin kendi çıktısı): `artifacts/verification/verify-summary.log`.
**Toplam: 418 otomatik test + 33 yığın kontrolü; başarısız 0, atlanan 0.** Hiçbir test atlanmadı, hiçbir iddia (assertion) geçmesi için gevşetilmedi.

## 2. İstenen final kontrolleriyle eşleme

| # | Kontrol | Kanıt |
|---|---|---|
| 1 | Kilitli restore + Release build; birim ve gerçek PostgreSQL entegrasyon; kritiklerde atlama yok | Yukarıdaki tablo; atlanan = 0 |
| 2 | Web tip denetimi / lint / üretim build / bileşen testleri | Yukarıdaki tablo |
| 3 | RLS gerçek çalışma zamanı rolüyle ham SQL + kapsamın yeniden kullanım/istisna/yeniden denemede davranışı; şirketler arası FK ve HTTP sahipliği | `RlsSqlTests` (14), `DbRunnerTests` (4), `EfFilterTests` (6), `CompositeFkTests` (2), `CrossTenantRequestTests` (3), `TenantAccessTests` (6) |
| 4 | A/B farklı kullanıcı ve aynı kullanıcı farklı şirket tarayıcı testi; iki paralel sekme; önbellek/yarış/çıkış/rol iptali | `e2e/isolation.spec.ts` (5), `e2e/revocation.spec.ts` (1); bileşen: `tenantSwitch`, `sessionCache`, `states` |
| 5 | Demo kayıt → şirket → davet → müşteri → talep → kota → plan değişikliği; operatör akışı yalnız metadata | `e2e/journey.spec.ts` (ürün sayfası → kayıt → posta kutusundan doğrulama → şirket → müşteri → talep + yorum + durum → davet ve davetlinin kabulü → plan değişikliği → dışa aktarma ve indirme → çıkış), `e2e/platform.spec.ts` (4), `PlatformTests` (3). **Not:** kota dolu ekranı tarayıcıda değil, bileşen testinde (`states.test.tsx`) ve API testlerinde (`QuotaTests`, 7) doğrulandı |
| 6 | Webhook yinelenen / sırasız / yanıt kaybı ve terminal kurtarma | `WebhookTests` (9), `PlanChangeTests` (10): `DuplicateEvent_SingleEffect`, `OutOfOrder_OlderSequence_DoesNotRollBack`, `Timeout_BecomesUnknown_ReconcileAppliesOnce`, `UnresolvedOutcome_NeedsResolution_OperatorResolvesTerminal` |
| 7 | Asenkron export / silme / worker kurtarma ve şirket izolasyonu | `ExportTests` (7), `TenantDeletionTests` (6), `OutboxTests` (8) |
| 8 | Boş veritabanı + önceki aşamadan yükseltme + model tutarlılığı + idempotent betiğin gerçekten uygulanması | `MigrationTests`: `Applies_ToEmptyDatabase`, `Upgrades_FromEachPreviousMigration_WithData`, `Model_HasNoPendingChanges`, `IdempotentScript_AppliesTwice`, `EveryMigration_HasUpgradeSeed` |
| 9 | Compose HTTPS duman: yeniden başlatmada kalıcılık, veritabanı kesintisi/toparlanma, seed; en az yetki / root değil | `stack-smoke.log`: kalıcılık (3), kesinti 503 ve toparlanma (7), seed yinelenebilir, root değil + salt-okunur + yetenekler bırakılmış (api, worker), roller superuser/BYPASSRLS değil, imajda sır yok |
| 10 | Kritik ekranlar üç genişlikte; otomatik erişilebilirlik / klavye; elle sınırlar açık | `e2e/a11y.spec.ts` (6): 20 ekran/durum × 360/768/1440 axe taraması + açık silme iletişim kutusu (WCAG 2.1 A/AA kuralları; serious/critical ihlal = 0) + klavye turu; 61 ekran görüntüsü `artifacts/verification/screenshots/`. Elle sınırlar: §5 |
| 11 | Sır / yazarlık / bağımlılık taraması | §4 |
| 12 | Kendi test yığınlarının temizliği; geliştirme verisi korunur | §6 |

## 3. İlk başarısızlıklar, kök nedenler, düzeltmeler ve son geçiş

Aşama bazında ayrıntılı kayıt [implementation-progress.md](implementation-progress.md) içindedir. Son geçişte hepsi kapalıdır. Önemlileri:

| Aşama | İlk belirti | Kök neden | Düzeltme | Son durum |
|---|---|---|---|---|
| 2 | Worker kapsayıcısı sürekli yeniden başlıyordu; `compose up --wait` fark etmedi | Worker'ın DI grafiğinde yalnız API'de bulunan servis; worker'ın sağlık denetimi yoktu | API/worker kayıtları ayrıldı, heartbeat sağlık denetimi, `HostCompositionTests` | Geçti |
| 3 | Talep oluşturma 500 | Denetim satırının FK'si aynı `SaveChanges`'ta talepten önce yazılıyordu | Önce talep, sonra denetim (aynı transaction) | Geçti |
| 4 | Üye çıkarma, süren içerik yazmalarıyla kilitleniyordu | Şirket satırında `FOR UPDATE`, içerik yazmalarının FK kilidiyle (`FOR KEY SHARE`) çakışıyor | Üyelik işlemleri `FOR NO KEY UPDATE`; `FOR UPDATE` yalnız yaşam döngüsünde | Geçti |
| 5 | Eşzamanlı plan değişikliğinden sonra kota limiti 0 okundu | Tek deyimde join + `FOR SHARE OF` (EvalPlanQual) satır döndürmedi | Kilit ve limit okuması iki ayrı deyim | Geçti |
| 7 | Çöken export işi hemen `Failed` oldu | Hata sonrası aynı turda yeniden denenip deneme hakkı tükendi | Artan gecikmeli yeniden deneme; geçici dosya adı kiralama token'ını taşıyor | Geçti |
| 8 | Tarayıcı testi: rolü düşürülen kullanıcıda **maskesiz müşteri e-postası** ekranda kaldı | Önceki rolle doldurulmuş istemci önbelleği rol değişiminde atılmıyordu | Rol / yetki listesi / erişim modu değişince şirketin içerik önbelleği atılıyor | Geçti (bileşen + tarayıcı testi) |
| 8 | Tarayıcı testi 17/21 | Dört test seçicisi belirsizdi (aynı etiket birden çok öğeye uyuyordu) | Seçiciler role ve tabloya daraltıldı (iddialar değişmedi) | Geçti |
| 9 | Hatalı isteklerin log satırında uç şablonu ve şirket kimliği yoktu | Hata işleyici rota bilgisini istekten siliyor | Özgün uç `IExceptionHandlerFeature`'dan okunuyor | Geçti |
| 10 | Depo taraması 4 bulgu verdi | Yanlış pozitif: doğrulama iletisi cümleleri ve betikteki bir regex | Desenler daraltıldı (boşluksuz değer; `\|` ile başlayan değer hariç); pozitif kontrol yeniden doğrulandı | 0 bulgu |

Test duyarlılığı (bilerek bozup yakalandığını görme): istek loguna sorgu dizesi eklendiğinde `Logs_DoNotContain_PiiCanaries` kırıldı; depoya sahte sır/imza içeren dosya konduğunda tarama 4 bulgu verdi. İkisi de geri alındı.

## 4. Tarama kapsamı (kontrol 11)

| Tarama | Araç | Kapsam | Sonuç |
|---|---|---|---|
| Sır desenleri | `scripts/scan-secrets.ps1` (kendi regex'leri: özel anahtar başlığı, bağlantı dizesi parolası, sabit parola ataması, atanmış API anahtarı/sır, AWS/GitHub/Slack token'ı, JWT) | git'in gördüğü 311 dosya (izlenen + izlenmeyen, `.gitignore` dışı) | 0 bulgu. 2 dosya gerekçeli istisna: yalnız tek kullanımlık test veritabanının rol parolaları ve test webhook sırrı |
| Yasak dosyalar | aynı betik | `.local*/`, `*.pfx/pem/key/p12`, `.env*` (örnek hariç), demo parola dosyaları | 0 |
| Sentetik veri | aynı betik | Ayrılmış olmayan alan adındaki e-postalar | 0 |
| Yazarlık | aynı betik | Ortak yazar ve otomatik üretim imzası satırları; otomatik araç adları (iki olgu belgesi dışında); paket meta verisinde sahip | 0 imza; sahip TCoderr (NuGet + npm) |
| Git durumu | aynı betik | commit sayısı, remote, depo-yerel kimlik ayarı | commit 0, remote 0, yerel kimlik ayarı 0 |
| NuGet zafiyetleri | `dotnet list package --vulnerable --include-transitive` | Doğrudan + geçişli | Bilinen zafiyet yok |
| npm zafiyetleri | `npm audit --audit-level=low` | Üretim + geliştirme bağımlılıkları | 0 |
| İmaj içeriği | `stack-smoke.ps1` (root olarak `find`, pozitif kontrollü) + `docker history` | Çalışma zamanı imajı | Sır/sertifika/demo parolası dosyası yok |

**Tarayıcıların bulgu vermemesi "güvenli" demek değildir.** Desen tabanlı tarama bilinmeyen biçimdeki sırları kaçırabilir; zafiyet denetimi koşu anındaki
veritabanlarını yansıtır; kapsayıcı imajının işletim sistemi paketleri taranmadı; bağımsız sızma testi yapılmadı.

## 5. Çalıştırılmayanlar ve elle yapılanlar (açık beyan)

- **Uzak CI: tanımlı, uzakta çalıştırılmadı.** `.github/workflows/ci.yml` yazıldı; push yapılmadığı için GitHub Actions'ta hiç koşmadı. Yerel sonuçlar uzak CI sonucu değildir.
- **Ekran okuyucu ile elle test yapılmadı.** Erişilebilirlik kanıtı otomatik axe taraması ve otomatik klavye turudur; bu "erişilebilir" iddiası değildir.
- **Kıyaslama (benchmark) yapılmadı;** performans, verim veya kapasite iddiası yoktur. Eşzamanlılık testleri doğruluğu sınar, ölçeklenebilirliği değil.
- Tarayıcı testleri yalnız masaüstü Chrome'da koşuldu (mobil yalnız görüntü alanı boyutuyla).
- Elle bakılanlar: üç ekran görüntüsü (`talepler-1440`, `musteriler-viewer-360`, `talep-detay-768`) gözle incelendi; `design/preview.html` içeriği metin olarak denetlendi
  (10 "Demo önizleme" etiketi, dış kaynak 0, betik 0). Kalan ekran görüntüleri tek tek gözle incelenmedi.
- Geliştirme yığını (`kulvar`, port 8843) bu final koşuda **başlatılmadı**; uçtan uca doğrulama aynı imaj ve aynı Compose dosyasıyla izole yığınlarda yapıldı.
- Tarayıcıda denenmeyen akışlar: [known-limitations.md](known-limitations.md) "Arayüz ve tarayıcı testlerinin kapsamadıkları".

## 6. Temizlik ve komşu projeler (kontrol 12)

Koşu sonunda doğrulandı:

- `kulvar-smoke` ve `kulvar-e2e` projelerine ait kapsayıcı, ağ ve volume kalmadı; `.local-smoke` ve `.local-e2e` dizinleri yok; Testcontainers kapsayıcısı kalmadı.
- Komşu projeler değişmedi: `loca-api-1`, `loca-postgres-1`, `lst-api-1`, `lst-postgres-1` çalışıyor; volume'lar `loca_keys`, `loca_pgdata`, `lst_dpkeys`, `lst_pgdata` yerinde
  (duman testi koşu öncesi/sonrası listeleri karşılaştırır: 2 kontrol geçti).
- Geride kalanlar (bilerek): Docker imajı `kulvar:local` ve taban imajlar (yeniden build'i hızlandırır; `docker image rm kulvar:local` ile silinebilir);
  `.local/` (geliştirme yığını için üretilmiş yerel sırlar, sertifika ve demo parolaları — git'e girmez); `src/web/node_modules`.
- Geliştirme yığını çalışmıyor ve geliştirme volume'u yok; korunacak geliştirme verisi oluşmadı.

## 7. Yapılmayan işlemler

Commit, push, pull request, uzak depo, dağıtım, bulut kaynağı veya ücretli servis **oluşturulmadı**. Git deposu yereldir (dal `main`, commit 0, remote 0);
global git kimliği okunmadı ve değiştirilmedi. Gerçek e-posta, SMS veya ödeme gönderilmedi.
