# Uygulama İlerlemesi

Bu dosya her aşama sonunda güncellenir. Test kayıtları yalnız yanlarındaki kaynak parmak izi için geçerlidir
(`scripts/source-fingerprint.ps1`; docs/artifacts/design hariç, git'in yok saymadığı tüm dosyalar).

## Aşama 0 — Çalışma alanı ve araçlar (2026-10-10) — TAMAM

- Başlangıç dizini `D:\Multi-Tenant SaaS Platform` boştu; etkili `CLAUDE.md`/`AGENTS.md` yok. Git deposu yoktu.
- Proje dizini oluşturuldu: `D:\Multi-Tenant SaaS Platform\multi-tenant-saas-platform`; `git init -b main` (commit yok, remote yok).
- Git kimliği (global, değiştirilmedi): `TCoderr`.
- Araçlar: .NET SDK 10.0.401, Node 24.18.0 / npm 12.1.0, Git 2.55.0, Docker Engine 29.8.2 (çalışıyor), Compose v5.5.1, Chrome ve Edge kurulu.
- Çalışan komşu yığınlar (dokunulmadı): `loca` (127.0.0.1:8643), `lst` (127.0.0.1:8443) ve volume'ları.
- Bu ürün için seçilenler: compose projesi `kulvar`, port **8843**; izole duman yığını `kulvar-smoke` port **8853**, izole tarayıcı testi yığını `kulvar-e2e` port **8863**.
- Envanter: aynı SaaS ürünü yerelde yok (bkz. 01 §7). Uzak envantere bakılmadı.
- Geliştirici HTTPS sertifikası mevcut ve dışa aktarılabilir; sertifika deposu değiştirilmedi.
- Yerel araç: `dotnet-ef 10.0.12` proje yerel manifestinde (`dotnet-tools.json`); global kurulum yapılmadı.

## Tasarım (2026-10-10) — TAMAM

Belgeler: `docs/01`–`09`, `docs/adr/0001`–`0007`, `docs/skills-and-sources.md`.
Ana kararlar: paylaşılan şema + RLS (FORCE) + bileşik FK; dört DB rolü; URL'den şirket + her istekte üyelik; sunucu tarafı oturum + CSRF;
tek deyimle kota tüketimi; epoch korumalı faturalama; kiralamalı outbox + fencing; worker'da token üretimi.

## Aşama 1 — Temel, Identity ve üyelik (2026-10-10) — TAMAM

Kararlar (tasarıma ek):
- Yapılandırma, kayıt anında değil kullanım anında okunur (DB çalıştırıcıları, hız sınırları): host kurulurken eklenen kaynaklar görülür.
- `memberships`/`tenants` RLS politikalarında kullanıcı kapsamı ile şirket kapsamı birbirini dışlar (şirket kapsamına girince başka
  şirketlerdeki kendi üyeliklerim görünmez).
- Platform operatörü yalnız `--migrate --grant-operator <e-posta>` ile atanır; sahip rol `users` tablosunda yalnız aranan e-postanın satırını görür.

Dosyalar: `src/Kulvar.Platform/*` (DbScope/DbSession/DbRunner, AppDbContext, Sql, Problems, AccessRules, TenantContext, Outbox, Audit, Telemetry),
`src/Kulvar.Modules.Identity/*`, `src/Kulvar.Modules.Tenancy/*` (TenantExecutor, uçlar, RolePolicy), `src/Kulvar.Api/Hosting/*`,
`src/Kulvar.Api/Migrations/*_IdentityTenancy.cs` + `Migrations/Sql/M0001.cs`, `db/bootstrap/*`, `tests/*`.

Kabul: bir kullanıcı iki şirket ✔ · iki kullanıcı izolasyonu ✔ · logout sonrası kopyalanmış cookie reddi ✔ · son Owner değişmezi ✔ ·
self-escalation ve platform rolü signup'ı yok ✔ · gerçek PostgreSQL (Testcontainers 18.6, kısıtlı roller) ✔

## Aşama 2 — DB izolasyonu ve Docker/CI temeli (2026-10-10) — TAMAM

Dosyalar: `tests/Kulvar.IntegrationTests/Stage2/*` (RLS doğrudan SQL, şema kataloğu, trigger, DbRunner, EF filtresi, migration, host bileşimi),
`Dockerfile`, `compose.yaml`, `deploy/postgres/init/01-bootstrap.sh`, `scripts/local-stack.ps1|.sh`, `scripts/stack-smoke.ps1`,
`scripts/source-fingerprint.ps1`, `.github/workflows/ci.yml`, `src/Kulvar.Api/Seeding/*`, `src/web/*` (iskelet).

İlk başarısızlıklar ve düzeltmeler:
| # | Belirti | Kök neden | Düzeltme |
|---|---|---|---|
| 1 | Test host'u başlamadı: "Body was inferred…" | Bağlantı dizeleri servis kaydı sırasında okunuyordu; test yapılandırması henüz eklenmemişti, `AppDb` kaydedilmedi | Çalıştırıcılar ve hız sınırları tembel okumaya çevrildi |
| 2 | Compose'da worker çöküp yeniden başlıyordu, `--wait` bunu görmedi | Worker DI grafiğinde API'ye özgü `TenantExecutor` vardı; worker'ın healthcheck'i yoktu | API'ye özgü kayıtlar ayrıldı (`AddTenancyApi`); worker heartbeat healthcheck'i; `HostCompositionTests` |
| 3 | `reset` kendi volume'larını reddetti | PowerShell 5.1 yerel komuta giden iç çift tırnakları düşürdü; etiket şablonu bozuldu (koruma kapalı yönde başarısız oldu) | Etiket JSON olarak okunuyor |
| 4 | Demo seed 6. kullanıcıda durdu | Signup hız sınırı (5/dk) | Seed istemcisi 429'da `Retry-After` kadar bekleyip yeniden deniyor |

Kabul: kapsamsız ham SQL fail closed ✔ · A kapsamında B için S/I/U/D reddi ✔ · bağlantı yeniden kullanımı A→B→kapsamsız (aynı backend pid) ✔ ·
hata/iptal/yeniden denemede sızıntı yok ✔ · boş yığın, yeniden başlatma kalıcılığı, DB kapat/aç (503 → 200) ✔ · seed idempotent ✔

Doğrulama (kaynak parmak izi `c25d2e33c41bd490`, 107 dosya; 2026-10-10):
| Komut | Exit | Sonuç |
|---|---|---|
| `dotnet restore Kulvar.slnx --locked-mode` | 0 | — |
| `dotnet build Kulvar.slnx -c Release --no-restore` | 0 | 0 uyarı, 0 hata |
| `dotnet test --project tests/Kulvar.UnitTests -c Release --no-build` | 0 | 72 toplam / 72 geçti / 0 başarısız / 0 atlandı |
| `dotnet test --project tests/Kulvar.IntegrationTests -c Release --no-build` | 0 | 65 toplam / 65 geçti / 0 başarısız / 0 atlandı |
| `scripts/stack-smoke.ps1` (proje `kulvar-smoke`, port 8853) | 0 | 31 kontrol / 0 başarısız (log: `artifacts/verification/stack-smoke.log`) |

Not: duman testi parmak izi hesaplanmadan hemen önce, aynı kaynakla koşuldu; sonrasında yalnız belgeler ve parmak izi betiği eklendi.

## Aşama 3 — Müşteri ve destek talepleri (2026-10-10) — TAMAM

Kararlar (tasarıma ek):
- Idempotency yanıt gövdesi `text` olarak saklanır (jsonb anahtar sırasını/boşlukları değiştirdiği için tekrar oynatma bayt bayt aynı olmuyordu).
- Maskeli gören rol (Viewer) e-posta üzerinden arayamaz: arama, maskelenen alanı tahmin etme aracı olmasın.
- API JSON'u varsayılan (gevşetilmemiş) kodlayıcıyla yazılır: `< > &` kaçırılır.
- Talep numarası sayaç satırı ilk talepte `INSERT … ON CONFLICT DO UPDATE` ile oluşur (ayrı kurulum adımı/backfill gerekmez).
- Kota kapısı arayüzü (`IUsageGate`) talep oluşturma transaction'ına yerleştirildi; uygulaması Aşama 5'te (Billing).

Dosyalar: `src/Kulvar.Platform/Http/Idempotency.cs`, `Http/Paging.cs`; `src/Kulvar.Modules.Customers/*`; `src/Kulvar.Modules.Support/*`
(TicketWorkflow, uçlar, üyelik kaldırılınca atama boşaltma); migration `*_CustomersSupport.cs` + `Migrations/Sql/M0002.cs`;
`tests/Kulvar.IntegrationTests/Stage3/*`, `tests/Kulvar.UnitTests/TicketWorkflowTests.cs`; `scripts/verify.ps1`.

İlk başarısızlıklar ve düzeltmeler:
| # | Belirti | Kök neden | Düzeltme |
|---|---|---|---|
| 1 | Talep oluşturma 500 | Denetim kaydının `(tenant_id, ticket_id)` FK'si EF modelinin dışında; EF etkinliği talepten önce ekledi | Talep kaydedilir, sonra etkinlik (aynı transaction) |
| 2 | Tekrar oynatılan yanıt ilkinden farklı | `jsonb` gövdeyi yeniden biçimlendiriyor | Sütun `text` |
| 3 | Talebi olan müşteriyi silme 500 | `ON DELETE RESTRICT` ihlali 23001 verir (23503 değil) | İki kod da 409 `customer_has_tickets` |
| 4 | Yorum JSON'unda ham `<script` | Minimal API varsayılan kodlayıcısı gevşetilmiş | `JavaScriptEncoder.Default` |
| 5 | `invalid_transition` yanıtı ayrıştırılamadı | Uzantı alanı `status`, ProblemDetails `status` ile çakıştı | Alan adı `ticketStatus` |
| 6 | (test) imleç 400 / FK yerine unique ihlali | Test yardımcısı iki `cursor` parametresi gönderdi; taşıma denemesi numara tekilliğine takıldı | Testler düzeltildi |

Kabul: şirketler arası FK ve istek saldırıları ✔ · imleç kapsamı ✔ · eski düzenleme 412 / eksik 428 ✔ · replay tek etki ✔ ·
rollback'te kısmi veri yok ✔ · geçiş/rol kuralları ✔

Doğrulama (`scripts/verify.ps1`; kaynak parmak izi `9b494f20fa2eda3e`, 124 dosya; 2026-10-10):
| Adım | Exit | Sonuç |
|---|---|---|
| `dotnet restore --locked-mode` | 0 | — |
| `dotnet build -c Release` | 0 | 0 uyarı |
| Birim testleri | 0 | 99 / 99 geçti / 0 başarısız / 0 atlandı |
| Entegrasyon testleri (PostgreSQL 18, kısıtlı roller) | 0 | 108 / 108 geçti / 0 başarısız / 0 atlandı |

## Aşama 4 — Üyelik/davet yaşam döngüsü (2026-10-10) — TAMAM

Kararlar (tasarıma ek):
- Üyelik/koltuk işlemlerinin şirket satırı kilidi `FOR NO KEY UPDATE`'tir (trigger dahil). `FOR UPDATE`, şirkete FK ile bağlanan devam eden
  tüm içerik yazmalarıyla (`FOR KEY SHARE`) çakışıyordu. `FOR UPDATE` yalnız yaşam döngüsü değişikliğine (silme/temizleme) ayrıldı.
- Posta işleyicileri yeniden teslimatta etkisizdir (`dedupe_key` ön kontrolü; davette `token_hash` doluysa atlanır).
- Yeni izin `ownership.accept` (her rol, yalnız `Full` mod); kabulde hedefin aktör olduğu ayrıca doğrulanır.
- Davet önizlemesi (`/api/invites/lookup`) davet e-postasını maskeli döndürür.

Dosyalar: `src/Kulvar.Modules.Tenancy/Domain/Invites.cs`, `Features/InviteEndpoints.cs`, `Features/OwnershipEndpoints.cs`,
`Features/TenancyMailHandlers.cs`; migration `*_InvitesOwnership.cs` + `Migrations/Sql/M0003.cs`; `tests/Kulvar.IntegrationTests/Stage4/*`.

İlk başarısızlıklar ve düzeltmeler:
| # | Belirti | Kök neden | Düzeltme |
|---|---|---|---|
| 1 | Migration `pk_memberships`'i düşürmeye çalıştı | Davet yapılandırması üyelik anahtarını önce "alternatif anahtar" olarak tanıttı; EF birincil anahtarın adını değiştirdi | `HasKey(...).HasName("pk_memberships")`; migration yeniden üretildi |
| 2 | Kaldırma, devam eden yazmayı üyelik satırında değil şirket satırında bekledi | Şirket satırında `FOR UPDATE`, müşteri eklemenin FK kilidiyle çakıştı | `FOR NO KEY UPDATE` |

Kabul: yanlış e-posta/şirket ✔ · paralel çift kabul → tek üyelik ✔ · iptal/süresi dolmuş token ✔ · son Owner paralel kaldırma/devir ✔ ·
rol kaldırma eski oturum/idempotency ile aşılamaz ✔ · davet koltuk yarışı ✔

Doğrulama (`scripts/verify.ps1`; kaynak parmak izi `2d02d2378d3ec6b9`, 133 dosya; 2026-10-10):
| Adım | Exit | Sonuç |
|---|---|---|
| `dotnet restore --locked-mode` | 0 | — |
| `dotnet build -c Release` | 0 | 0 uyarı |
| Birim testleri | 0 | 99 / 99 geçti / 0 başarısız / 0 atlandı |
| Entegrasyon testleri | 0 | 134 / 134 geçti / 0 başarısız / 0 atlandı |

## Aşama 5 — Planlar, kota ve entitlement (2026-10-10) — TAMAM

Kararlar (tasarıma ek):
- Kota limiti iki ayrı deyimle okunur (abonelik `FOR SHARE`, sonra plan sürümü). Join'li tek deyim, kilit beklerken abonelik güncellenince
  satır döndürmüyordu (READ COMMITTED yeniden değerlendirmesi join edilen tabloyu yeniden aramaz).
- Zamanlayıcı sorgusu silinen şirketleri dışlar ve en eski vadeye göre sıralar (parti dolup diğerleri aç kalmasın).
- Geçici `PreBillingEntitlementReader` kaldırıldı; entitlement her istekte `subscriptions` + `plan_versions`'tan okunur.
- Migration 0004 veri taşır (mevcut şirketlere Trial): `tenants` üzerinde aynı transaction içinde geçici `NO FORCE` → backfill → `FORCE`.

Dosyalar: `src/Kulvar.Modules.Billing/*` (Subscription, PlanCatalog, UsagePeriod, EntitlementReader, UsageGate, SubscriptionTimers, uçlar,
posta işleyici); `src/Kulvar.Api/Hosting/OtherHosts.cs` (LifecycleWorker); migration `*_BillingEntitlements.cs` + `Migrations/Sql/M0004.cs`;
`tests/Kulvar.IntegrationTests/Stage5/*`.

İlk başarısızlık ve düzeltme:
| # | Belirti | Kök neden | Düzeltme |
|---|---|---|---|
| 1 | Düşürme × oluşturma yarışında kota yanıtı `limit: 0` | Kilitli satır yeni sürümüyle döndü ama join'li plan satırı yeniden aranmadı; sorgu boş döndü (kapalı yönde ama yanlış limit) | Limit iki deyimle okunuyor |

Kabul: son kota hakkına 25 eşzamanlı istek → tam 1 başarı ✔ · iki şirket ayrı kota ✔ · replay/rollback sayaç doğru ✔ · UTC ay sınırı ✔ ·
düşürme yarışı (bariyerli) ve limit üstü veri silinmez ✔ · askıda viewer okur / owner kurtarma yolu açık ✔

Doğrulama (`scripts/verify.ps1`; kaynak parmak izi `a7e767ca435bc8df`, 141 dosya; 2026-10-10):
| Adım | Exit | Sonuç |
|---|---|---|
| `dotnet restore --locked-mode` | 0 | — |
| `dotnet build -c Release` | 0 | 0 uyarı |
| Birim testleri | 0 | 99 / 99 geçti / 0 başarısız / 0 atlandı |
| Entegrasyon testleri | 0 | 156 / 156 geçti / 0 başarısız / 0 atlandı |

## Aşama 6 — Simüle faturalama ve webhook (2026-10-10) — TAMAM

Kararlar (tasarıma ek):
- Her yeni plan işlemi aboneliğin `epoch`'unu ilerletir (iptal ve silme isteğine ek olarak): yalnız en son istenen işlem uygulanabilir.
  Böylece çözüm bekleyen eski bir işlem şirketi kilitlemez; şirket yeni işlem isteyebilir, eskinin geç başarısı uygulanamaz.
- "Aynı anda tek işlem" kısıtı yalnız sağlayıcı sonucu bekleyen durumları (`Pending`, `Unknown`) kapsar.
- Operatör "refund" eylemi işlemi iade kuyruğuna geri gönderir (worker iade eder); "void" ve "close" doğrudan terminaldir. Gerekçe ≥ 10 karakter.
- Webhook alımı `kulvar_platform` rolüyle yalnız inbox'a yazar; etkiyi worker uygular. Simüle sağlayıcının durumu `fakepsp` şemasında, yalnız worker erişir.
- Operatör uçları (`/api/platform/*`) bu aşamada eklendi (çözüm yolu testi için): şirket metadata'sı, askı/geri açma, işlem çözümü, ölü işler, denetim, simülasyon.

Dosyalar: `src/Kulvar.Modules.Billing/Domain/BillingOperation.cs`, `Provider/*`, `Processing.cs`, `Webhook.cs`, `PlanChangeEndpoints.cs`;
`src/Kulvar.Platform/Audit/PlatformAuditEvent.cs`; `src/Kulvar.Modules.Operations/PlatformEndpoints.cs`; migration `*_BillingOperations.cs` +
`Migrations/Sql/M0005.cs`; `tests/Kulvar.IntegrationTests/Stage6/*`, `tests/Kulvar.UnitTests/BillingRulesTests.cs`.

Kabul: bilinmeyen eşleşme / geçersiz imza / çelişen gövde ✔ · yinelenen / sırasız ✔ · yanıt kaybı sonrası yeniden deneme tek tahsilat ✔ ·
reddedilen plan entitlement açmaz ✔ · iptal/silme sonrası geç başarı yeniden etkinleştirmez (iade) ✔ · çözülemeyen sonuç operatör
eylemiyle terminal (void / close / refund→…) ✔

Doğrulama (Debug koşusu; Release kapısı Aşama 7 sonunda birlikte kaydedilir): birim 131/131, entegrasyon 179/179, 0 atlandı.

## Aşama 7 — Worker, export, yaşam döngüsü (2026-10-10) — TAMAM (arka uç)

Kararlar (tasarıma ek):
- Kapatma isteği mesajlar arasında gözlenir; başlanmış mesaj tamamlanır (işleme iptal belirteci geçirilmez), başlanmamışlar kiralama dolunca alınır.
- Export üretimi tek transaction'dadır ve boyunca şirket satırı `FOR SHARE` kilitlidir; temizleme `FOR UPDATE` ile bekler. Geçici dosya adı
  kiralama token'ını taşır (`<iş>.zip.<token>.tmp`); devralan worker eskinin dosyasına yazamaz. Hata sonrası iş hemen değil, artan gecikmeyle yeniden alınır.
- İndirme: oturum + üyelik + `export.manage` + işi isteyen kullanıcı + 60 sn'lik tek kullanımlık token. Aynı şirketin başka Owner'ı da indiremez.
- Temizleme adım adım (tablo başına transaction) ve yinelenebilir; `Purging` durumundaki şirket kimseye görünmez, iptal edilemez.
  Mezar taşında `tenants` (ad temizlenir), `subscriptions` (Cancelled, epoch+1), `billing_operations` kalır.
- Demo seed gerçek akışları kullanır: plan değişikliği (simüle sağlayıcı), davet/kabul (demo posta kutusu), müşteri/talep/geçiş/yorum.

Dosyalar: `src/Kulvar.Modules.Operations/*` (Exports, ExportRunner, CsvWriter, TenantPurger, OperationsModule, denetim ucu),
`src/Kulvar.Modules.Tenancy/Features/TenantEndpoints.cs` (silme isteği/iptali), `src/Kulvar.Platform/Tenancy/TenantRegistry.cs`,
migration `*_ExportsLifecycle.cs` + `Migrations/Sql/M0006.cs`, `src/Kulvar.Api/Seeding/DemoSeeder.cs`,
`tests/Kulvar.IntegrationTests/Stage7/*`, `tests/Kulvar.UnitTests/CsvWriterTests.cs`.

İlk başarısızlıklar ve düzeltmeler:
| # | Belirti | Kök neden | Düzeltme |
|---|---|---|---|
| 1 | Export çökme testinde iş `Failed` oldu | Hata sonrası iş aynı turda hemen yeniden denendi ve deneme sınırını tüketti | Yeniden deneme artan gecikmeyle (kiralama süresi olarak) planlanıyor |
| 2 | (tasarım) İki worker aynı geçici dosyaya yazabilirdi | Geçici dosya adı sabitti | Ad kiralama token'ını taşıyor |
| 3 | (test) Sayılar deterministik değildi | Paylaşılan test veritabanında başka testlerden kalmış export/silme işleri | Testler önce kuyruğu boşaltıyor veya hedef şirketi doğrudan işliyor |

Kabul: çökme/kiralama dolması/fencing'de çift etki yok ✔ · artefakt yetki/süre/path traversal/formül enjeksiyonu ✔ ·
A temizlenirken B ve ortak kullanıcı etkilenmez ✔ · silme × iş/export yarışı (bariyerli) ve yarıda kesilip tekrar ✔ · şirketler arası posta kutusu sızıntısı yok ✔

Doğrulama (`scripts/verify.ps1`; kaynak parmak izi `622f6bf397ff70af`, 168 dosya; 2026-10-10):
| Adım | Exit | Sonuç |
|---|---|---|
| `dotnet restore --locked-mode` | 0 | — |
| `dotnet build -c Release` | 0 | 0 uyarı |
| Birim testleri | 0 | 148 / 148 geçti / 0 başarısız / 0 atlandı |
| Entegrasyon testleri | 0 | 203 / 203 geçti / 0 başarısız / 0 atlandı |

Not: yığın duman testi (Docker) bu parmak iziyle henüz yeniden koşulmadı; Aşama 9/10'da koşulacak.

## Aşama 8 — React arayüzü — TAMAM

Kararlar:
- Şirket kimliği yalnız URL'den (`/t/:tenantId/…`) okunur; bellekte "aktif şirket" yoktur. Sorgu anahtarları `['u', kullanıcı, 't', şirket, …]`.
- Oturum/CSRF belirteci tarayıcı deposuna yazılmaz (ESLint kuralı zorlar); CSRF belirteci yalnız bellekte durur ve kimlik değişince yenilenir.
- Çıkış, 401 ve kullanıcı değişiminde sorgu önbelleği tamamen temizlenir. Rol, yetki listesi veya erişim modu değişince o şirketin içerik önbelleği atılır.
- İyimser güncelleme yok: değişiklikten sonra ilgili sorgular geçersiz kılınır; sayaç ve yetkiler her zaman sunucudan gelir.
- Yetkiler sunucunun bildirdiği `permissions` listesinden gösterilir; yapılamayan eylem gizlenmez, nedeniyle birlikte devre dışıdır.
- Katı CSP (`style-src 'self'`): satır içi stil/betik yok; yazı tipleri ayrı dosya (`assetsInlineLimit: 0`). IBM Plex (OFL-1.1) lisans metinleri uygulamayla sunulur.
- Tarayıcı testleri kendi izole yığınında koşar (`kulvar-e2e`, port 8863, `.local-e2e`); hız sınırları yalnız orada yükseltilir; koşu sonunda yığın ve dizin silinir.

Dosyalar: `src/web/src/{api,app,session,workspace,components,lib,pages,styles,test}/**`, `src/web/e2e/*.spec.ts`, `src/web/playwright.config.ts`,
`src/web/public/licenses/*`, `design/preview.html`, `scripts/e2e.ps1`, `artifacts/verification/screenshots/` (61 görüntü).

İlk başarısızlıklar ve düzeltmeler:
| # | Belirti | Kök neden | Düzeltme |
|---|---|---|---|
| 1 | Lint 135 hata | `react-hooks` v7: form yardımcısı ref döndürüyordu; effect içinde eşzamanlı `setState` | Odak `useId` ile bulunuyor; oturum yenilemesi promise zincirine çevrildi |
| 2 | Bileşen testi (1) | Seçenekler yüklenmeden `selectOptions` çağrısı | Test seçeneği bekliyor |
| 3 | Tarayıcı testi 17/21 | Dört seçici belirsizdi (etiket birden çok öğeye uyuyordu) | Seçiciler role ve tabloya daraltıldı; iddialar değişmedi |
| 4 | Tarayıcı testi 20/21 — **gerçek arayüz hatası** | Rolü Viewer'a düşen kullanıcıda önceki rolle önbelleğe alınmış maskesiz müşteri e-postası ekranda kalıyordu | Rol/yetki/erişim modu değişince şirketin içerik önbelleği atılıp yeniden okunuyor; bileşen testi eklendi |

Kabul: gerçek durumlar (yükleniyor/boş/hata/yetkisiz/çevrimdışı/kota/askı/silme bekliyor) ✔ · şirket bağlamı her ekranda görünür ✔ · 360/768/1440 ✔ ·
axe serious/critical = 0 ve klavye turu ✔ · A/B izolasyonu, iki sekme, çıkış, rol iptali tarayıcıda ✔ · statik önizleme "Demo önizleme" etiketli, dış kaynak yok ✔

Arayüzün çevresinden dolaştığı arka uç boşlukları ve tarayıcıda denenmeyen akışlar `docs/known-limitations.md` içindedir.

## Aşama 9 — Sağlamlaştırma — TAMAM (arka uç kısmı; Aşama 8 ile paralel yürütüldü)

Kararlar:
- İstek logu (`Kulvar.Request`): yalnız yöntem, eşleşen uç şablonu, durum kodu, süre, kullanıcı ve şirket kimliği. Ham yol/sorgu/gövde yazılmaz
  (arama terimi ve export indirme token'ı sorgu dizesindedir). Hata işleyici rota bilgisini istekten sildiği için özgün uç `IExceptionHandlerFeature`'dan okunur.
- Çerçevenin işlenmemiş istisna logu kapatıldı (istisna iletisi veri taşıyabilir); kendi işleyicimiz tür + sqlstate + yığın izi + traceId yazar.
- Depo taraması tek betikte: sır desenleri, repoya girmemesi gereken dosyalar, ayrılmış olmayan alan adında e-posta, yazarlık/imza kuralı,
  git durumu (commit/remote/yerel kimlik yok), NuGet ve npm zafiyet denetimi.
- Yığın duman testine "imajda sır/sertifika/demo parolası dosyası yok; build geçmişinde sır yok" denetimi eklendi.

Dosyalar: `src/Kulvar.Api/Hosting/ApiHost.cs` (`RequestLog`), `src/Kulvar.Api/appsettings.json`, `src/Kulvar.Platform/Http/Problems.cs`,
`tests/Kulvar.IntegrationTests/Stage9/HardeningTests.cs`, `scripts/scan-secrets.ps1`, `scripts/stack-smoke.ps1`, `README.md`, `.env.example`,
`docs/tenant-isolation-verification.md`, `docs/known-limitations.md`; belgeler 02/04/05/06/09 ve ADR-0004/0005 uygulamayla eşitlendi.

İlk başarısızlıklar ve düzeltmeler:
| # | Belirti | Kök neden | Düzeltme |
|---|---|---|---|
| 1 | Hatalı girişin (401) log satırında uç `(eşleşmedi)` ve şirket kimliği boş | `UseExceptionHandler` hata yolunda uç ve rota değerlerini istekten siler | Özgün uç/rota değerleri `IExceptionHandlerFeature`'dan okunuyor; test hata yolunu da doğruluyor |
| 2 | (test) Operatör listesinde şirket adı bulunamadı | JSON kodlayıcı ASCII dışı harfleri `\uXXXX` kaçırır; test ham metinde aradı | Test JSON'u ayrıştırıp alanı karşılaştırıyor |
| 3 | (tarama) Sır taraması kod atamalarını (`password = …`) ve psql değişkenlerini bulgu saydı | Desen fazla genişti | Bağlantı dizesi biçimi ve sabit dizgi atamaları ayrı desenler; psql yer tutucusu hariç |
| 4 | (tarama) Birim testinde ayrılmış olmayan alan adı (`b.co`) | Test girdisi | `b.example` |

Test duyarlılığı kontrolleri (bilerek bozup yakalandığını görme):
- İstek loguna sorgu dizesi eklendi → `Logs_DoNotContain_PiiCanaries` sızan indirme token'ını yakalayıp kırıldı → geri alındı.
- Depoya sahte bağlantı parolası, özel anahtar başlığı, gerçek görünümlü e-posta ve ortak yazar satırı içeren geçici dosya kondu → tarama 4 bulgu verdi (exit 1) → dosya silindi, temiz koşu 0 bulgu.

Kabul: log/iz/metrikte kişisel veri ve yüksek kardinalite yok ✔ · operatör yalnız metadata, rol düzeyinde de engelli ✔ · DB erişilemezken 503, sahte başarı/sahte 401 yok ✔ ·
hız sınırı 429 + Retry-After, kotadan ayrı ✔ · güvenlik başlıkları ve cookie bayrakları ✔ · sır/imza taraması 0 bulgu, NuGet/npm zafiyet denetimi temiz ✔

Ara doğrulama (`scripts/verify.ps1`, 2026-10-10; arayüz dosyaları o sırada değişmekte olduğundan parmak izi kesin değildir — bağlayıcı sonuçlar Aşama 10'dadır):
restore 0 · build 0 uyarı · birim 148/148 · entegrasyon 212/212 · 0 atlanan.

## Aşama 10 — Final doğrulama — TAMAM

Tüm kapılar tek kaynak parmak izinde (`b30967daa618d93e`, 210 dosya) art arda koşuldu; koşu başında ve sonunda parmak izi aynıydı (2026-10-10 21:19–21:24 UTC).

| Kapı | Exit | Sonuç |
|---|---|---|
| `dotnet restore --locked-mode` | 0 | — |
| `dotnet build -c Release --no-incremental` | 0 | 0 uyarı |
| Birim testleri | 0 | 148 / 148 / 0 başarısız / 0 atlanan |
| Entegrasyon testleri (PostgreSQL 18.6, kısıtlı roller) | 0 | 212 / 212 / 0 / 0 |
| Web: typecheck, lint, üretim build | 0 | temiz |
| Web: bileşen testleri | 0 | 37 / 37 / 0 / 0 |
| `scripts/stack-smoke.ps1` | 0 | 33 / 33 kontrol |
| `scripts/e2e.ps1` | 0 | 21 / 21 / 0 / 0 (kararsız 0) |
| `scripts/scan-secrets.ps1` | 0 | 0 bulgu; NuGet + npm zafiyet denetimi temiz |

Bu aşamadaki düzeltme: depo taraması ilk koşuda 4 yanlış pozitif verdi (doğrulama iletisi cümleleri, betikteki regex) → desenler daraltıldı, pozitif kontrol
yeniden doğrulandı, betik değiştiği için **tüm kapılar yeni parmak iziyle baştan koşuldu**.

Temizlik: `kulvar-smoke` / `kulvar-e2e` kaynakları ve Testcontainers kapsayıcıları kalmadı; `loca` ve `lst` değişmedi. Geliştirme yığını çalışmıyor.
Uzak CI çalıştırılmadı (push yok). Commit, push, PR, remote, dağıtım yapılmadı. Ayrıntı: `docs/final-verification.md`.
