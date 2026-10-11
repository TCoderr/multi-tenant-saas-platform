# 03 — Hedef Mimari

## 1. Genel görünüm

```
 Tarayıcı (React SPA, aynı origin)
        │ HTTPS, cookie + CSRF
        ▼
 ┌──────────────────────── kulvar-api (ASP.NET Core, non-root) ───────────────────────┐
 │ Kimlik doğrulama → şirket çözümleme + güncel üyelik → izin → iş transaction'ı       │
 │ DB bağlantıları: kulvar_app (kullanıcı/şirket kapsamlı), kulvar_platform (metadata) │
 └───────────────┬────────────────────────────────────────────────────────────────────┘
                 │                                   ▲ imzalı webhook (HMAC)
                 ▼                                   │
        PostgreSQL 18 (RLS, bileşik FK)      ┌───────┴──────── kulvar-worker ───────────┐
                 ▲                           │ outbox, inbox, mutabakat, zamanlayıcılar, │
                 └───────────────────────────│ export, temizleme, simüle sağlayıcı       │
                                             │ DB bağlantısı: kulvar_worker              │
                                             └───────────────────────────────────────────┘
 Tek seferlik: kulvar-migrator (kulvar_owner) → şema + isteğe bağlı demo seed
```

Tek image, dört kullanım: `(varsayılan) API`, `--worker`, `--migrate [--seed]`, `--probe` (healthcheck).

## 2. Modüller ve bağımlılık yönleri

```
 Kulvar.Api (bileşim kökü, migration sahibi, CLI, worker barındırma)
   └─► Operations ─► Support ─► Customers ─► Platform
            │           └────► Tenancy ───► Identity ─► Platform
            ├─────────► Billing ──────────────────────► Platform
            └─────────► Tenancy, Identity, Customers
```

| Proje | Sorumluluk | Bağımlılık |
|---|---|---|
| `Kulvar.Platform` | Çekirdek: `DbScope`, transaction çalıştırıcı, `AppDbContext`, izin/rol tipleri, ProblemDetails, idempotency, outbox yazıcı, denetim yazıcı, saat | — |
| `Kulvar.Modules.Identity` | Kullanıcı, oturum, doğrulama token'ı, platform operatörü | Platform |
| `Kulvar.Modules.Tenancy` | Şirket, üyelik, davet, sahiplik; **şirket erişim çözümleyici** (`ITenantExecutor` uygulaması) | Platform, Identity |
| `Kulvar.Modules.Customers` | Müşteri | Platform |
| `Kulvar.Modules.Support` | Talep, yorum | Platform, Customers, Tenancy |
| `Kulvar.Modules.Billing` | Plan, abonelik, kullanım, ödeme işlemi, webhook inbox, simüle sağlayıcı; `IEntitlementReader` uygulaması | Platform |
| `Kulvar.Modules.Operations` | Outbox işleyici, posta kutusu, export, şirket temizleme, platform operatörü uçları | diğer modüller |
| `Kulvar.Api` | Host, ara katmanlar, EF migration'ları, seed, worker döngüleri | hepsi |

Kurallar:
- Modüller arası döngü yoktur. Tenancy'nin koltuk limiti için Billing'e ihtiyacı `Platform`'daki `IEntitlementReader` arayüzüyle çözülür (Billing uygular); Billing'in izin kontrolü `Platform`'daki `ITenantExecutor` ile çözülür (Tenancy uygular).
- Alan kuralları (`Domain/` klasörleri) EF/HTTP tiplerine başvurmaz.
- Modüller arası okuma join'i yalnız bağımlılık yönünde yapılır (ör. Support → Customers: talep listesinde müşteri adı). Ters yönde olay/outbox kullanılır.
- Tek `AppDbContext` vardır (`Platform`). Her modül kendi `IEntityTypeConfiguration` sınıflarını ve tablolarını tanımlar; modeli `Kulvar.Api` birleştirir. **Migration'ların tek sahibi `Kulvar.Api/Migrations`**'tır; RLS/rol/trigger SQL'i de orada, migration içinde durur.
- Bir tabloya yalnız sahibi olan modül yazar. İstisna: `Operations`'taki şirket temizleme işi (tüm tablolardan siler) ve çekirdek yazıcılar (outbox, denetim, idempotency).

## 3. İstek ve transaction yaşam döngüsü

Ortam (ambient) transaction veya istek başına tek DbContext yoktur. Her iş birimi şu çalıştırıcıdan geçer:

```
 DbRunner.RunAsync(scope, work):
   deneme 1..3 (yalnız 40001/40P01 için yeniden):
     bağlantı aç → BEGIN
     SELECT set_config('app.user_id', @u, true) [, diğer kapsam anahtarları]
     work(DbSession)        ← DbSession.EnterTenantAsync(tenantId) yalnız bir kez çağrılabilir
     COMMIT   (hata/iptal → ROLLBACK; bağlantı havuza kapsam bilgisi olmadan döner)
```

Şirket kapsamlı istek (`TenantExecutor`):
1. Oturum cookie'si doğrulanır (oturum satırı DB'den, her istekte).
2. Transaction açılır, yalnız `app.user_id` ayarlanır.
3. `(tenantId, userId)` üyeliği ve şirket satırı **kullanıcı kapsamlı** RLS politikasıyla okunur. Yoksa → 404 `tenant_not_found`.
4. Üyelik doğrulandıktan **sonra** `app.tenant_id` ayarlanır (`set_config(..., true)`), DbContext filtresi aynı değere sabitlenir.
5. Yazma isteğinde aktörün üyelik satırı `FOR SHARE` kilitlenir (eşzamanlı rol/kaldırma işlemi bu isteği bekler).
6. Abonelik/entitlement okunur, erişim modu ve izin değerlendirilir (403 `forbidden` / `access_mode`).
7. İş kodu çalışır (idempotency, alan kuralı, outbox, denetim) ve `SaveChanges` + `COMMIT`.

Okuma istekleri de aynı transaction kalıbını kullanır; kapsam `SET LOCAL` eşdeğeri olduğundan commit/rollback ile kaybolur.
Yeniden denemede yeni transaction açılır ve kapsam baştan kurulur.

Transaction sahipliği: uç nokta işleyicisi (komut) tek transaction'ın sahibidir. Worker'da her mesaj/iş kendi transaction'ıdır.

## 4. Komut/sorgu ayrımı

Basit tutulur: her uç nokta bir statik işleyicidir. Komutlar alan nesnesini yükler, kuralı çalıştırır, kaydeder.
Sorgular `AsNoTracking` projeksiyonlarıdır. Ayrı okuma modeli, MediatR veya genel repository yoktur.

## 5. Teknoloji kararları

| Seçim | İhtiyaç | Daha basit alternatif | Maliyet | Ne zaman değişir |
|---|---|---|---|---|
| Modüler monolit, tek süreç ailesi | Net sınırlar, tek transaction | Tek proje | Proje sayısı, kilit dosyaları | Ekip/dağıtım bağımsızlığı gerçekten gerekirse servis ayrımı |
| PostgreSQL 18 + EF Core 10 (Npgsql) | RLS, bileşik FK, `SKIP LOCKED` | — | RLS/rol yönetimi | — |
| Paylaşılan şema + `tenant_id` + RLS | İzolasyon + tek migration | Yalnız uygulama filtresi | Politika/rol karmaşıklığı | Büyük müşteri için ayrı DB zorunluluğu (ADR-0001) |
| Aynı origin React SPA + cookie + CSRF | Tarayıcıda token saklamamak, sunucu tarafı iptal | JWT (localStorage) | CSRF yönetimi | Üçüncü taraf/mobil istemci gerekirse token tabanlı API eklenir (ADR-0003) |
| Kendi oturum tablosu + ASP.NET Core `PasswordHasher` | Sunucu tarafı iptal, RLS ile uyumlu sade tablolar | Tam ASP.NET Core Identity + EF store | Kilit/oturum mantığı elle | MFA/harici giriş gerekirse Identity veya harici IdP |
| DB outbox/inbox + kiralamalı worker | Atomik etki + mesaj | Doğrudan çağrı | Gecikme, poll | Hacim poll'u taşırırsa kuyruk altyapısı |
| TanStack Query | Şirket+kullanıcı kapsamlı önbellek anahtarı | Elle fetch | Bağımlılık | — |
| Polling (gerçek zamanlı yok) | Plan/export durumu | SignalR | Birkaç saniye gecikme | Canlı işbirliği gerekirse (ayrı iptal/izolasyon testiyle) |
| Yerleşik `Meter`/`ActivitySource`, JSON log | Gözlemlenebilirlik temeli | — | Dışa aktarıcı yok | Toplayıcı kurulduğunda OTLP paketi |

Kullanılmayanlar ve nedeni: MediatR/genel repository (dolaylılık, kazanç yok), Redis (önbelleğe gerek yok; entitlement her istekte DB'den),
Kafka/RabbitMQ (tek DB outbox yeterli), Dapper (EF + sınırlı parametreli SQL yeterli), JWT (ADR-0003), mikroservis.

## 6. Önbellek ve entitlement

Sunucuda entitlement önbelleği **yoktur**: her şirket kapsamlı istek aboneliği ve plan sürümünü DB'den okur
(aynı transaction, birincil anahtar araması). Böylece geçersiz kılma/TTL/negatif sonuç sorunu oluşmaz.
İstemci tarafında TanStack Query anahtarı `['u', userId, 't', tenantId, …]` biçimindedir; sayaçlar yalnız gösterimdir, karar sunucudadır.

## 7. Arka plan işleri

| Döngü | İş | Adillik/sınır |
|---|---|---|
| OutboxDispatcher | Mesaj kirala, işle, tamamla | Parti başına en çok 20 mesaj, şirket başına en çok 5; deneme sınırı 8 |
| InboxProcessor | Webhook olaylarını uygula | Olay başına tek transaction |
| BillingReconciler | `Unknown` ve `RefundPending` işlemler | Deneme sınırı, üstel bekleme |
| LifecycleTimers | Trial/ödemesiz dönem bitişi, silme zamanı gelen şirketler, artefakt/anahtar/oturum temizliği | Parti sınırı |
| ExportRunner | Export işleri | Aynı anda şirket başına 1 |
| ProviderSimulator | Simüle sağlayıcının webhook'larını API'ye gönderir | Yalnız simülasyon açıkken |

Worker her işten önce şirket yaşam döngüsünü yeniden okur; mesaj gövdesindeki `tenant_id` yetki üretmez, yalnız kapsam ister
ve kaynak (ör. davet satırı) o kapsamda bulunamazsa iş "konu yok" olarak kapanır.
Kapatma: `CancellationToken` ile parti arası durur; yarım kalan iş kiralama süresi dolunca yeniden alınır.

## 8. Dağıtım (yalnız yerel)

Docker Compose projesi `kulvar`: `postgres`, `migrator`, `api` (127.0.0.1:8843), `worker`. Volume'lar: `pgdata`, `dpkeys`, `exports`.
Container'lar root olmayan kullanıcıyla, salt-okunur kök dosya sistemiyle ve yetenekler (capabilities) bırakılmış olarak çalışır.
Uzak dağıtım, bulut kaynağı yoktur.
