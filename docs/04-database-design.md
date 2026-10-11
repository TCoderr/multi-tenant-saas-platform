# 04 — Veritabanı Tasarımı

PostgreSQL 18. Şemalar: `app` (ürün), `fakepsp` (simüle ödeme sağlayıcısının kendi durumu; dış sistemi temsil eder).
Adlandırma snake_case. Tüm zaman sütunları `timestamptz` (UTC). Para `bigint` kuruş + `char(3)` para birimi (`TRY`).

## 1. Roller

| Rol | Kullanım | Özellikler |
|---|---|---|
| `postgres` | Yalnız container ilk kurulumu | superuser; uygulama kullanmaz |
| `kulvar_owner` | Migration ve operatör kaydı (tek seferlik `migrator`) | Şema/tablo sahibi. `NOSUPERUSER NOBYPASSRLS`. Tablolarda `FORCE ROW LEVEL SECURITY` olduğu için kiracı satırlarını **okuyamaz** |
| `kulvar_app` | API: kullanıcı/şirket kapsamlı istekler | `LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE`, sahip değil |
| `kulvar_platform` | API: platform operatörü uçları ve webhook alımı | Aynı kısıtlar. Müşteri/talep/yorum/üyelik/davet/kullanıcı tablolarında **hiç yetkisi yok** |
| `kulvar_worker` | Worker süreci | Aynı kısıtlar. Kuyruk/abonelik/şirket metadata'sını şirketler arası görür; içerik tablolarını yalnız şirket kapsamı ayarlıyken |

Roller ve parolaları container ilk kurulum betiği (`deploy/postgres/init/01-roles.sh`) ile oluşturulur; migration rol oluşturmaz,
yalnız var olduklarını doğrular ve yetki/politika verir. Testlerde aynı SQL, test fixture'ı tarafından superuser ile uygulanır.

## 2. Tablo sınıflandırması

| Tablo | Sınıf | `tenant_id` | RLS |
|---|---|---|---|
| `users`, `sessions`, `user_tokens`, `platform_operators` | Global kimlik | — | Kullanıcı kapsamlı |
| `tenants` | Global kayıt (registry) | (kendi `id`'si) | Üyelik/kapsam |
| `plans`, `plan_versions` | Global referans (public fiyat) | — | Yok (yalnız `SELECT`) |
| `memberships`, `invites`, `ownership_offers` | Kiracı | NOT NULL | Kiracı (+ kullanıcı/token kapsamlı okuma) |
| `customers`, `tickets`, `ticket_comments` | Kiracı içerik | NOT NULL | Kiracı |
| `subscriptions`, `billing_operations`, `usage_counters`, `usage_events`, `tenant_counters` | Kiracı faturalama | NOT NULL | Kiracı; worker/platform için metadata politikası |
| `audit_events`, `idempotency_keys`, `export_jobs` | Kiracı operasyon | NOT NULL | Kiracı; worker için kuyruk politikası (`export_jobs`) |
| `outbox_messages` | Kuyruk | NULL olabilir (kullanıcı kapsamlı mesaj) | app: yalnız `INSERT`; worker: tümü |
| `sink_messages` | Demo posta kutusu | NULL olabilir | app: yalnız kendi e-postası |
| `provider_events` | Webhook inbox (global) | çözümlenince dolar | platform: alım; worker: işlem; app: yok |
| `platform_audit_events` | Platform denetimi | NULL olabilir | platform/worker |
| `fakepsp.*` | Dış sistem simülasyonu | — | Yalnız worker |

Otomatik test (`SchemaConventionTests`) `app` şemasındaki her tablonun bu sınıflandırmada bulunduğunu, kiracı tablolarında
`tenant_id NOT NULL` olduğunu ve RLS'in `ENABLE + FORCE` edildiğini `pg_catalog` üzerinden doğrular.

## 3. Tablolar (özet)

### 3.1 Kimlik (global)

```
users(id uuid PK, email text, normalized_email text UNIQUE, display_name text, password_hash text,
      email_verified_at timestamptz NULL, failed_login_count int, lockout_until timestamptz NULL,
      created_at timestamptz, version int)
sessions(id uuid PK, user_id uuid → users RESTRICT, created_at, expires_at, revoked_at NULL)      IX(user_id)
user_tokens(id uuid PK, user_id → users RESTRICT, purpose text, token_hash bytea UNIQUE, expires_at, consumed_at NULL, created_at)
platform_operators(user_id uuid PK → users RESTRICT, created_at)
```

### 3.2 Şirket ve üyelik

```
tenants(id uuid PK, name text, status text CHECK IN ('Active','Suspended','DeletionPending','Purging','Deleted'),
        created_by_user_id uuid → users, created_at, suspended_reason text NULL,
        deletion_requested_at NULL, purge_after NULL, deleted_at NULL, version int)

memberships(tenant_id uuid → tenants RESTRICT, user_id uuid → users RESTRICT, PRIMARY KEY (tenant_id, user_id),
            role text CHECK IN ('Owner','Admin','Agent','Viewer'), status text CHECK IN ('Active','Removed'),
            created_at, removed_at NULL, version int)                                              IX(user_id)

invites(id uuid PK, tenant_id uuid NOT NULL → tenants, UNIQUE (tenant_id, id),
        email text, normalized_email text, role text CHECK IN ('Admin','Agent','Viewer'),
        status text CHECK IN ('Pending','Accepted','Revoked'), token_hash bytea NULL UNIQUE, sent_at NULL,
        invited_by_user_id uuid, FOREIGN KEY (tenant_id, invited_by_user_id) → memberships(tenant_id, user_id),
        accepted_by_user_id uuid NULL, created_at, expires_at, accepted_at NULL, revoked_at NULL)
        UNIQUE (tenant_id, normalized_email) WHERE status = 'Pending'

ownership_offers(id uuid PK, tenant_id uuid NOT NULL, from_user_id, to_user_id, demote_self bool,
        status text CHECK IN ('Pending','Accepted','Cancelled','Declined'), created_at, expires_at, resolved_at NULL,
        FOREIGN KEY (tenant_id, from_user_id) → memberships, FOREIGN KEY (tenant_id, to_user_id) → memberships)
        UNIQUE (tenant_id) WHERE status = 'Pending'
```

### 3.3 Müşteri ve destek

```
customers(id uuid PK, tenant_id uuid NOT NULL → tenants, UNIQUE (tenant_id, id),
          name text, company_title text NULL, email text NULL, normalized_email text NULL, phone text NULL, notes text NULL,
          search_text text GENERATED ALWAYS AS (lower(name || ' ' || coalesce(company_title,'') || ' ' || coalesce(email,''))) STORED,
          created_by_user_id uuid, created_at, updated_at, version int)
          UNIQUE (tenant_id, normalized_email) WHERE normalized_email IS NOT NULL      ← şirket içinde tekil; A ve B aynı e-postayı tutabilir
          IX (tenant_id, created_at DESC, id DESC), IX (tenant_id, name, id)

tickets(id uuid PK, tenant_id uuid NOT NULL → tenants, UNIQUE (tenant_id, id), number int, UNIQUE (tenant_id, number),
        customer_id uuid NOT NULL,      FOREIGN KEY (tenant_id, customer_id)        → customers(tenant_id, id)       RESTRICT,
        assignee_user_id uuid NULL,     FOREIGN KEY (tenant_id, assignee_user_id)   → memberships(tenant_id, user_id) RESTRICT,
        created_by_user_id uuid NOT NULL, FOREIGN KEY (tenant_id, created_by_user_id) → memberships(tenant_id, user_id) RESTRICT,
        title text, description text, priority text CHECK (...), status text CHECK (...),
        created_at, updated_at, resolved_at NULL, closed_at NULL, reopen_count int, version int)
        IX (tenant_id, created_at DESC, id DESC), IX (tenant_id, updated_at DESC, id DESC),
        IX (tenant_id, status, created_at DESC, id DESC), IX (tenant_id, assignee_user_id, status), IX (tenant_id, customer_id)

ticket_comments(id uuid PK, tenant_id uuid NOT NULL, ticket_id uuid NOT NULL, author_user_id uuid NOT NULL, body text, created_at,
        FOREIGN KEY (tenant_id, ticket_id)      → tickets(tenant_id, id)            RESTRICT,
        FOREIGN KEY (tenant_id, author_user_id) → memberships(tenant_id, user_id)   RESTRICT)
        IX (tenant_id, ticket_id, created_at, id)
```

Bileşik FK'lerin anlamı: bir talep yalnız **aynı şirketin** müşterisine, bir atama yalnız **aynı şirketin** üyelik satırına
bağlanabilir. `tenant_id`'yi başka şirkete çevirip yabancı `customer_id` vermek veritabanında FK ihlalidir (RLS'ten bağımsız).
Atananın *aktif* üye olması uygulama kuralıdır (transaction içinde kontrol + üyelik kaldırılırken aynı transaction'da atamalar boşaltılır).

### 3.4 Faturalama ve kullanım

```
plans(code text PK, name text, sort_order int)
plan_versions(id uuid PK, plan_code → plans, version int, UNIQUE (plan_code, version), price_minor bigint, currency char(3) CHECK = 'TRY',
              seat_limit int, monthly_ticket_limit int, export_enabled bool, trial_days int NULL, is_current bool)

subscriptions(tenant_id uuid PK → tenants, plan_version_id → plan_versions, status text CHECK (...), epoch int,
              trial_ends_at NULL, grace_ends_at NULL, current_period_end NULL,
              provider_subscription_id text NULL UNIQUE, provider_sequence bigint, updated_at, version int)

billing_operations(id uuid PK, tenant_id uuid NOT NULL → tenants, merchant_reference text UNIQUE,
              target_plan_version_id → plan_versions, amount_minor bigint, currency char(3), status text CHECK (...), epoch int,
              requested_by_user_id uuid → users, simulated_outcome text NULL, attempts int, next_attempt_at NULL,
              failure_code text NULL, resolution_note text NULL, resolved_by_user_id uuid NULL, created_at, updated_at, completed_at NULL, version int)
              UNIQUE (tenant_id) WHERE status IN ('Pending','Unknown','RefundPending')

usage_counters(tenant_id uuid, metric text, period_start date, used int CHECK (used >= 0), PRIMARY KEY (tenant_id, metric, period_start))
usage_events(tenant_id uuid, metric text, source_id uuid, period_start date, occurred_at, PRIMARY KEY (tenant_id, metric, source_id))
tenant_counters(tenant_id uuid PK → tenants, next_ticket_number int)

provider_events(provider_event_id text PK, payload_hash bytea, payload jsonb, received_at, status text, tenant_id uuid NULL,
                attempts int, next_attempt_at NULL, processed_at NULL, note text NULL)
```

Kota tüketimi tek deyimdir (önce-oku-sonra-yaz yok):

```sql
INSERT INTO app.usage_counters AS c (tenant_id, metric, period_start, used)
VALUES (@tenant, 'tickets_created', @period, 1)
ON CONFLICT (tenant_id, metric, period_start)
DO UPDATE SET used = c.used + 1 WHERE c.used < @limit
RETURNING used;          -- satır dönmezse kota doludur
```

`@limit` aynı transaction'da `subscriptions` satırı `FOR SHARE` kilitlenerek okunur; eşzamanlı plan değişikliği (`FOR UPDATE`) bu
transaction'ı bekler. `@limit = 0` durumunda `INSERT` yolu engellensin diye uygulama deyimi çalıştırmadan reddeder.

### 3.5 Operasyon

```
outbox_messages(id uuid PK, tenant_id uuid NULL, user_id uuid NULL, type text, payload jsonb, status text CHECK IN ('Pending','Done','Dead'),
                attempts int, available_at, lease_owner text NULL, lease_until NULL, lease_token bigint, last_error text NULL, created_at, processed_at NULL)
                IX (available_at) WHERE status = 'Pending'
sink_messages(id uuid PK, tenant_id uuid NULL, recipient_normalized_email text, kind text, subject text, body text,
              dedupe_key text UNIQUE, created_at)                                      IX (recipient_normalized_email, created_at DESC)
export_jobs(id uuid PK, tenant_id uuid NOT NULL → tenants, UNIQUE (tenant_id, id), requested_by_user_id uuid,
            FOREIGN KEY (tenant_id, requested_by_user_id) → memberships, status text CHECK (...), created_at, started_at NULL, completed_at NULL,
            expires_at NULL, size_bytes bigint NULL, sha256 text NULL, download_token_hash bytea NULL, download_token_expires_at NULL,
            attempts int, lease_owner text NULL, lease_until NULL, lease_token bigint, failure_code text NULL)
audit_events(id uuid PK, tenant_id uuid NOT NULL → tenants, actor_user_id uuid NULL, action text, target_type text, target_id uuid NULL,
             ticket_id uuid NULL, FOREIGN KEY (tenant_id, ticket_id) → tickets(tenant_id, id) RESTRICT, data jsonb, occurred_at)
             IX (tenant_id, occurred_at DESC, id DESC), IX (tenant_id, ticket_id, occurred_at, id)
platform_audit_events(id uuid PK, actor_user_id uuid NULL, action text, tenant_id uuid NULL, data jsonb, occurred_at)
idempotency_keys(tenant_id uuid, user_id uuid, operation text, key text, PRIMARY KEY (tenant_id, user_id, operation, key),
                 request_hash bytea, response_status int, response_body jsonb, created_at, expires_at)
```

`audit_events.data` ve outbox gövdeleri kişisel veri/sır içermez (kimlikler, rol adları, durum adları).

## 4. Silme davranışları

Tüm FK'ler `RESTRICT`/`NO ACTION`'dır; kademeli (cascade) silme yoktur. Böylece yanlışlıkla bir üst satırı silmek veriyi sessizce götüremez.
Şirket temizleme işi açık sırayla siler (bkz. §8). Kullanıcı hesabı silme kapsam dışıdır.

## 5. Eşzamanlılık ve sayfalama

- İyimser eşzamanlılık: `version int` sütunu, EF concurrency token. HTTP'de `ETag: "<version>"` / `If-Match`. Eksik → 428, eski → 412.
- Kilit sırası (deadlock'u azaltmak için): `tenants` satırı (üyelik işlemleri) → aktör üyeliği → `subscriptions` → sayaçlar → içerik satırları.
  Sıra dışı durumda PostgreSQL deadlock'u algılar (40P01); çalıştırıcı işi yeni transaction'da tekrarlar.
- Sayfalama keyset'tir: `(sıralama sütunu, id)`. İmleç; şirket, kullanıcı, liste türü, filtre özeti ve sıralamayı içerir ve
  Data Protection ile mühürlenir. Uyuşmayan imleç `400 invalid_cursor`. İmleç doğrulansa bile sorgu her zaman şirket filtresi + RLS altındadır.

## 6. Row-Level Security

Yardımcı fonksiyonlar (SECURITY DEFINER **değil**, sabit `search_path`):

```sql
CREATE FUNCTION app.current_tenant_id() RETURNS uuid LANGUAGE sql STABLE
  SET search_path = pg_catalog AS $$ SELECT nullif(current_setting('app.tenant_id', true), '')::uuid $$;
CREATE FUNCTION app.current_user_id() RETURNS uuid LANGUAGE sql STABLE
  SET search_path = pg_catalog AS $$ SELECT nullif(current_setting('app.user_id', true), '')::uuid $$;
```

Kapsam ayarlanmamışsa fonksiyon `NULL` döner; `tenant_id = NULL` hiçbir satırla eşleşmez (okuma boş, yazma `WITH CHECK` ihlali).
Geçersiz değer `uuid` dönüşüm hatasıdır. İkisi de **fail closed**'dır.

Kiracı tabloları için tek kalıp:

```sql
ALTER TABLE app.customers ENABLE ROW LEVEL SECURITY;
ALTER TABLE app.customers FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON app.customers FOR ALL TO kulvar_app, kulvar_worker
  USING      (tenant_id = app.current_tenant_id())
  WITH CHECK (tenant_id = app.current_tenant_id());
```

`USING` SELECT/UPDATE/DELETE için görünür satırı, `WITH CHECK` INSERT/UPDATE sonucunu sınırlar: A kapsamında B satırı okunamaz,
güncellenemez, silinemez; B `tenant_id`'li satır eklenemez; A satırının `tenant_id`'si B'ye çevrilemez.

Kapsam anahtarları (hepsi transaction-yerel `set_config(..., true)`):

| Anahtar | Kim ayarlar | Ne açar |
|---|---|---|
| `app.user_id` | Oturum doğrulandıktan sonra (cookie bütünlük korumalı) | Kullanıcının kendi satırları ve kendi üyelikleri |
| `app.tenant_id` | Üyelik doğrulandıktan sonra (istisnalar aşağıda) | O şirketin satırları |
| `app.login_email` | Login/signup sırasında, doğrulanacak adres | Yalnız o adresin `users` satırı |
| `app.invite_token_hash` | Davet kabulünde, sunulan token'ın hash'i | Yalnız o hash'e sahip davet satırı |

Döngüsel bağımlılığın çözümü: üyelik çözümleyici şirket kapsamı **olmadan** çalışmak zorundadır. Bunun için `memberships` ve `tenants`
okuması kullanıcı kapsamıyla da mümkündür; başka hiçbir tabloya kullanıcı kapsamı şirket verisi açmaz.

```sql
-- memberships: şirket kapsamı VEYA (şirket kapsamı yokken) kendi üyeliklerim. İki kapsam birbirini dışlar:
-- şirket kapsamına girildikten sonra başka şirketlerdeki kendi üyeliklerim de görünmez (sayım/liste sorguları karışmaz).
CREATE POLICY memberships_select ON app.memberships FOR SELECT TO kulvar_app, kulvar_worker
  USING (tenant_id = app.current_tenant_id()
         OR (app.current_tenant_id() IS NULL AND user_id = app.current_user_id()));
CREATE POLICY memberships_insert ON app.memberships FOR INSERT TO kulvar_app, kulvar_worker
  WITH CHECK (tenant_id = app.current_tenant_id());
CREATE POLICY memberships_update ON app.memberships FOR UPDATE TO kulvar_app, kulvar_worker
  USING (tenant_id = app.current_tenant_id()) WITH CHECK (tenant_id = app.current_tenant_id());
CREATE POLICY memberships_delete ON app.memberships FOR DELETE TO kulvar_worker      -- yalnız şirket temizleme
  USING (tenant_id = app.current_tenant_id());

-- tenants: kapsamdaki şirket VEYA (şirket kapsamı yokken) aktif üyesi olduğum şirketler
CREATE POLICY tenants_select ON app.tenants FOR SELECT TO kulvar_app
  USING (id = app.current_tenant_id()
         OR (app.current_tenant_id() IS NULL AND EXISTS (
               SELECT 1 FROM app.memberships m
               WHERE m.tenant_id = tenants.id AND m.user_id = app.current_user_id() AND m.status = 'Active')));
CREATE POLICY tenants_insert ON app.tenants FOR INSERT TO kulvar_app WITH CHECK (id = app.current_tenant_id());
CREATE POLICY tenants_update ON app.tenants FOR UPDATE TO kulvar_app
  USING (id = app.current_tenant_id()) WITH CHECK (id = app.current_tenant_id());
CREATE POLICY tenants_meta ON app.tenants FOR ALL TO kulvar_worker, kulvar_platform USING (true) WITH CHECK (true);

-- users: kendim, login araması veya kapsamdaki şirketin üyesi
CREATE POLICY users_select ON app.users FOR SELECT TO kulvar_app, kulvar_worker
  USING (id = app.current_user_id()
         OR normalized_email = nullif(current_setting('app.login_email', true), '')
         OR EXISTS (SELECT 1 FROM app.memberships m WHERE m.user_id = users.id AND m.tenant_id = app.current_tenant_id()));

-- invites: şirket kapsamı VEYA token hash'i bilinen tek satır
CREATE POLICY invites_token ON app.invites FOR SELECT TO kulvar_app
  USING (token_hash = decode(nullif(current_setting('app.invite_token_hash', true), ''), 'hex'));
```

`app.tenant_id`'nin "üyelik doğrulandıktan sonra" kuralının iki belgeli istisnası vardır; ikisinde de değer istemciden değil sunucudan gelir:
1. **Şirket oluşturma**: `id` sunucuda üretilir, kapsam o yeni `id`'ye ayarlanır, şirket + Owner üyeliği + abonelik eklenir.
2. **Davet kabulü**: davet satırı token hash kapsamıyla okunur ve doğrulanır (durum, süre, e-posta eşleşmesi); kapsam davetin kendi `tenant_id`'sine ayarlanır.

Worker ve platform politikaları (özet): `outbox_messages`, `export_jobs`, `subscriptions`, `billing_operations`, `provider_events`,
`tenants`, `sink_messages`, `platform_audit_events` üzerinde `kulvar_worker` için `USING (true)`; `kulvar_platform` için yalnız
`tenants`, `subscriptions` (SELECT), `billing_operations`, `provider_events`, `outbox_messages`, `platform_audit_events`, `plans`, `plan_versions`.
İçerik tablolarında (`customers`, `tickets`, `ticket_comments`, `audit_events`, …) worker yalnız kiracı politikasına tabidir.

### Yetkiler (GRANT) — özet

| Tablo | kulvar_app | kulvar_worker | kulvar_platform |
|---|---|---|---|
| users | S, I, U | S | — |
| sessions | S, I, U | S, D (süresi dolan) | — |
| user_tokens | S, U | S, I, D | — |
| platform_operators | S | — | — |
| tenants | S, I, U | S, U | S, U |
| memberships | S, I, U | S, D | — |
| invites | S, I, U | S, U, D | — |
| ownership_offers | S, I, U | S, D | — |
| customers | S, I, U, D¹ | S, D | — |
| tickets | S, I, U | S, D | — |
| ticket_comments | S, I | S, D | — |
| subscriptions | S, I, U | S, U | S |
| billing_operations | S, I | S, U | S, U |
| usage_counters, tenant_counters | S, I, U | S, D | — |
| usage_events | S, I | S, D | — |
| audit_events | S, I | S, I, D | — |
| idempotency_keys | S, I, U² | S, D | — |
| export_jobs | S, I, U | S, U, D | — |
| outbox_messages | I | S, I, U, D | S, U |
| sink_messages | S | S, I, D | — |
| provider_events | — | S, U | S, I, U |
| platform_audit_events | — | I | S, I |
| plans, plan_versions | S | S | S |
| fakepsp.subscriptions, operations, webhooks | — | S, I, U | — |
| fakepsp.simulation_requests | — | S, U | I³ |

¹ Yalnız talebi olmayan müşteri silinebilir (FK `RESTRICT`). Talepler ve yorumlar API rolüyle silinemez; yorumlar değiştirilemez (`U` yok).
² Yer tutucu satır önce eklenir (`ON CONFLICT DO NOTHING`), işlem bitince aynı transaction'da yanıtla güncellenir.
³ Yalnız simülasyon açıkken operatörün "sağlayıcı olayı iste" kaydı; olay yine imzalı webhook ucundan geri gelir.
S=SELECT, I=INSERT, U=UPDATE, D=DELETE. `TRUNCATE`, `REFERENCES`, `TRIGGER`, şema `CREATE` yetkisi hiçbir runtime rolünde yoktur.

Sahip (owner) ve FORCE: tablo sahibi normalde RLS'i atlar; `FORCE ROW LEVEL SECURITY` ile `kulvar_owner` da politikalara tabi olur ve
kendisine politika tanımlanmadığı için kiracı satırı göremez. Sahibe tanımlı yalnız iki dar politika vardır, ikisi de kurulum komutu
(`--grant-operator`) içindir: `platform_operators` üzerinde yazma ve `users` üzerinde **yalnız aranan e-postanın satırını** okuma
(`app.login_email` arama anahtarı kapsamı). Bedeli: ileride veri taşıyan bir migration gerekiyorsa o migration için
açık, dar ve geçici bir politika yazılmalıdır (bkz. ADR-0002). SECURITY DEFINER fonksiyon yoktur; dolayısıyla `search_path` kaçırma yüzeyi yoktur.

## 7. En az bir Owner trigger'ı

```sql
CREATE FUNCTION app.assert_tenant_has_owner() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, app AS $$
DECLARE t uuid; st text;
BEGIN
  IF TG_OP = 'DELETE' THEN t := OLD.tenant_id; ELSE t := NEW.tenant_id; END IF;
  SELECT status INTO st FROM app.tenants WHERE id = t FOR NO KEY UPDATE;
  IF st IS NULL THEN RAISE EXCEPTION 'tenant % is not visible in the current scope', t USING ERRCODE = '42501'; END IF;
  IF st IN ('Purging','Deleted') THEN RETURN NULL; END IF;
  IF NOT EXISTS (SELECT 1 FROM app.memberships WHERE tenant_id = t AND role = 'Owner' AND status = 'Active') THEN
    RAISE EXCEPTION 'tenant % must keep at least one active owner', t USING ERRCODE = '23514', CONSTRAINT = 'ck_tenant_has_owner';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER trg_tenant_has_owner AFTER INSERT OR UPDATE OR DELETE ON app.memberships
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION app.assert_tenant_has_owner();
```

Trigger çağıranın yetkisiyle çalışır; kapsam yoksa satır göremez ve hata verir (fail closed).

## 8. Şirket temizleme sırası

`Purging` durumunda, şirket kapsamı ayarlı worker transaction'larında, tekrar çalıştırılabilir (idempotent) biçimde:

`ticket_comments → audit_events → tickets → customers → usage_events → usage_counters → tenant_counters → idempotency_keys →
export_jobs (+ dosyalar) → ownership_offers → invites → sink_messages (şirketin) → outbox_messages (şirketin) → memberships`

Kalanlar (mezar taşı, kişisel veri içermez): `tenants` (ad `"(silindi)"` yapılır, `status = Deleted`), `subscriptions` (`Cancelled`),
`billing_operations`, `provider_events`, `platform_audit_events`. Global `users`/`sessions` satırlarına dokunulmaz; kullanıcının
başka şirketlerdeki üyelikleri RLS gereği zaten görünmez.

## 9. Migration politikası

- EF Core migration'ları `Kulvar.Api/Migrations` altında; RLS, rol yetkileri, trigger ve referans veri (planlar) migration SQL'idir.
- Doğrulama: boş DB'ye uygula; önceki aşamanın şemasına sentetik veri koyup yükselt; model/snapshot tutarlılığı; idempotent betiği
  (`migrations script --idempotent`) gerçek DB'ye iki kez uygula.
- Migration yalnız `kulvar_owner` ile çalışır; API ve worker açılışta migration yapmaz.
