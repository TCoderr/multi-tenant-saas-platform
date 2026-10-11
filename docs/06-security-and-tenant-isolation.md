# 06 — Güvenlik ve Şirket İzolasyonu

## 1. Tehdit modeli ve savunma katmanları

Korunan varlık: bir şirketin müşteri/talep/yorum/üyelik/plan verisi. Varsayılan saldırgan: **başka bir şirketin meşru, giriş yapmış üyesi**
(veya hiçbir şirkete üye olmayan kullanıcı) ve uygulama kodundaki olası hata (unutulmuş filtre, yanlış kimlik bağlama).

| # | Katman | Neyi durdurur | Neyi durdurmaz |
|---|---|---|---|
| 1 | Her istekte güncel üyelik + rol + erişim modu kontrolü (`TenantExecutor`) | Üye olmayanın erişimi, rol aşımı, kaldırılmış üyenin eski cookie'si | Kod bu çalıştırıcıyı atlarsa |
| 2 | EF global sorgu filtresi (`tenant_id = kapsam`) | LINQ sorgusunda unutulan `Where` | `IgnoreQueryFilters`, ham SQL, toplu işlemler |
| 3 | PostgreSQL RLS (`FORCE`, kısıtlı roller) | Filtresi unutulmuş/ham SQL, kapsamı olmayan bağlantı | Kapsamı kendisi ayarlayabilen **ele geçirilmiş runtime** |
| 4 | Bileşik FK / kapsamlı unique | Şirketler arası satır bağlama (yabancı müşteriye talep, yabancı üyeye atama) | — |
| 5 | Rol ayrımı (`app` / `platform` / `worker` / `owner`) | Operatör yolundan içerik okuma; runtime'ın şema değiştirmesi | Aynı süreçteki iki bağlantının birlikte ele geçirilmesi |

Dürüst sınır: RLS yetkilendirmenin yerine geçmez. `kulvar_app` rolüyle keyfî SQL çalıştırabilen bir saldırgan
`set_config('app.tenant_id', …)` çağırarak başka şirketin kapsamına girebilir. RLS burada **uygulama hatası sınıfına** karşı
ikinci bariyerdir, ele geçirilmiş sürece karşı değil. Bu ürün SQL enjeksiyonuna parametreli sorgularla karşı koyar; dinamik SQL yoktur.

## 2. Şirket çözümleme ve güven sınırı

- Şirket yalnız URL yolundan gelir. Bu değer **istektir, yetki değildir**: her istekte `(tenantId, userId)` üyeliği DB'den okunur.
- Cookie'de, oturumda veya sunucu belleğinde "aktif şirket" yoktur. İki sekme iki farklı şirkette bağımsız çalışır.
- Eksik/bozuk `tenantId` (GUID değil) → 404. Varsayılan şirket veya "tüm şirketler" kavramı yoktur.
- `DbScope` değişmezdir; `EnterTenantAsync` bir iş biriminde yalnız bir kez çağrılabilir, ikinci çağrı (farklı değerle) istisna atar.
- Tenant bilgisi singleton/statik alanda tutulmaz. DbContext havuzu (pooling) kullanılmaz; her iş birimi yeni context + yeni transaction'dır.
- Kimlik görünürlüğü: şirket ve kaynak kimlikleri (UUID) üyelere URL'lerde görünür ve gizli sayılmaz. Slug yoktur; şirket adına göre arama/keşif ucu yoktur.
- 403/404 politikası: üye olmayan için şirketin varlığı 404 ile gizlenir; üye için eksik izin 403'tür; şirket içindeki yabancı/olmayan kaynak 404'tür.

Global ve şirket kapsamlı uç listesi [05](05-api-contracts.md) §2–§4'tedir. `GET /api/me` ve `GET /api/me/memberships` şirket içeriği döndürmez
(yalnız şirket adı, rol, durum).

## 3. İzin matrisi

| İzin | Owner | Admin | Agent | Viewer | İzinli erişim modları |
|---|---|---|---|---|---|
| `workspace.read` | ✓ | ✓ | ✓ | ✓ | Full, Recovery, ReadOnly, Locked |
| `members.leave` | ✓¹ | ✓ | ✓ | ✓ | Full, Recovery, ReadOnly, Locked |
| `tenant.settings` | ✓ | — | — | — | Full |
| `tenant.delete` (iste) | ✓ | — | — | — | Full, Recovery |
| `tenant.delete.cancel` | ✓ | — | — | — | ReadOnly |
| `customers.read` | ✓ | ✓ | ✓ | ✓ | Full, Recovery, ReadOnly |
| `customers.pii` (maskesiz e-posta/telefon) | ✓ | ✓ | ✓ | — | — |
| `customers.write` | ✓ | ✓ | ✓ | — | Full |
| `customers.delete` | ✓ | ✓ | — | — | Full |
| `tickets.read` | ✓ | ✓ | ✓ | ✓ | Full, Recovery, ReadOnly |
| `tickets.write` | ✓ | ✓ | ✓² | — | Full |
| `members.read` | ✓ | ✓ | ✓ | ✓ | Full, Recovery, ReadOnly |
| `members.pii` (üye e-postası) | ✓ | ✓ | — | — | — |
| `members.invite` | ✓ | ✓³ | — | — | Full |
| `members.manage` (rol, kaldırma) | ✓ | ✓³ | — | — | Full, Recovery |
| `ownership.offer` | ✓ | — | — | — | Full |
| `billing.read` | ✓ | ✓ | — | — | Full, Recovery, ReadOnly |
| `billing.manage` | ✓ | — | — | — | Full, Recovery |
| `audit.read` | ✓ | ✓ | — | — | Full, Recovery, ReadOnly |
| `export.manage` | ✓ | — | — | — | Full, Recovery, ReadOnly (+ plan `exportEnabled`) |

¹ Başka aktif Owner varsa. ² Yalnız kendine atanmış veya atanmamış taleplerde. ³ Yalnız Agent/Viewer hedefleri ve rolleri.

Platform operatörü bu matriste yoktur: şirket üyesi değildir, şirket uçlarına erişemez. Yetkisi yalnız `/api/platform` uçlarıdır ve
bu uçların DB rolünün içerik tablolarında yetkisi yoktur. Operatör aynı zamanda bir şirketin üyesiyse o şirkette yalnız üyelik rolü kadar yetkilidir.

Arayüzde bir düğmenin gizlenmesi yetki değildir; her uç yukarıdaki kontrolü sunucuda yapar.

## 4. Kimlik, oturum, CSRF

- Parola: en az 12 karakter; ASP.NET Core `PasswordHasher` (PBKDF2-HMAC-SHA512, tuzlu). Parola loglanmaz.
- Signup/login yanıtları geneldir; var olan e-postayla signup aynı `202`'yi döndürür ve hesap sahibinin demo posta kutusuna bilgi iletisi düşer.
- Kilit: 5 hatalı deneme → 15 dk; kilitliyken yanıt yine `401 invalid_credentials`.
- Hız sınırı (IP başına sabit pencere): login 20/dk, signup 5/dk, webhook 120/dk, genel API 600/dk. Aşım `429` + `Retry-After`.
- Oturum: sunucu tarafı `sessions` satırı, 12 saat mutlak ömür. Cookie yalnız oturum ve kullanıcı kimliğini taşır (Data Protection ile
  şifreli+imzalı). **Her istekte** satır okunur: iptal edilmiş/süresi dolmuş oturum `401`. Logout satırı iptal eder; kopyalanmış cookie reddedilir.
- Rol/üyelik cookie'de **yoktur**; her istekte DB'den okunur. Üyeliği kaldırılan kullanıcı bir sonraki istekte 404 alır;
  devam eden yazma isteği üyelik satırındaki `FOR SHARE` kilidi nedeniyle kaldırma işleminden önce biter.
- CSRF: ASP.NET Core Antiforgery (çift gönderim + kimliğe bağlı token). Token `GET /api/auth/csrf` ile alınır; login ve logout sonrası
  kimlik değiştiği için yeniden alınır. Tüm güvensiz `/api` uçları doğrular (webhook hariç: cookie kullanmaz, HMAC imzalıdır).
- Güvenlik başlıkları: `Content-Security-Policy` (yalnız kendi origin; inline script yok), `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, HSTS.
- Tarayıcıda `localStorage`/`sessionStorage`'a token veya kişisel veri yazılmaz.

## 5. Davet güvenliği

- Token 32 bayt CSPRNG, base64url. Veritabanında yalnız SHA-256 hash'i; ham token yalnız demo posta kutusundaki iletide.
- Kabul koşulları (tek transaction, şirket satırı kilitli): durum `Pending`, süre dolmamış, oturum sahibinin **doğrulanmış** e-postası
  davet e-postasıyla (normalize) aynı, koltuk limiti uygun.
- Rol davet satırından gelir; kabul isteğinin gövdesinde rol alanı yoktur.
- Eşzamanlı iki kabul: şirket kilidi + `memberships` birincil anahtarı; biri kazanır, diğeri `409 invite_not_pending`.
- Davet listesi token döndürmez; Owner/Admin başkasının token'ını göremez.

Demo sınırı: gerçek posta sağlayıcısı yoktur. Demo posta kutusu "o adresin gelen kutusu"nu temsil eder ve o adresle kayıt olan hesaba
açılır; yani demo ortamında bir e-posta adresinin gerçek sahibi kanıtlanmaz. Üretimde gerçek sağlayıcı şarttır.

## 6. İstek, önbellek, iş ve log izolasyonu

| Yüzey | Kural |
|---|---|
| Idempotency | Anahtar `(tenant_id, user_id, operation, key)`; kayıt şirket kapsamlı tabloda ve üyelik/izin kontrolünden **sonra** okunur. Kaldırılmış üye eski yanıtı tekrar oynatamaz. |
| İmleç | Şirket + kullanıcı + liste + filtre özetiyle mühürlü; başka şirkette `400`. |
| Sunucu önbelleği | Yok (entitlement dahil). |
| İstemci önbelleği | Anahtar `['u', userId, 't', tenantId, …]`; logout ve kullanıcı değişiminde tümü temizlenir; `401` gelince temizlenir. Geç gelen A yanıtı yalnız A anahtarına yazılır, B ekranı B anahtarını okur. |
| Outbox | Uygulama rolü yalnız kendi kapsamındaki `tenant_id` ile ekleyebilir (RLS `WITH CHECK`), okuyamaz. |
| Worker | Gövdedeki `tenant_id` yetki üretmez: worker şirket durumunu yeniden okur, kapsamı ayarlar ve kaynağı o kapsamda arar. |
| Kilit/kiralama | Satır kilitleri ve `lease_token` satır bazlıdır; şirketler arası paylaşılan uygulama kilidi yoktur. |
| Adillik | Outbox partisi şirket başına en çok 5 mesaj alır; export şirket başına aynı anda 1. Bu temel bir sınırdır, "gürültülü komşu çözüldü" iddiası değildir. |
| Export artefaktı | Yol yalnız sunucu üretimi GUID'lerden kurulur: `<kök>/<tenantId>/<jobId>.zip`. İndirme: oturum + üyelik + izin + işi isteyen kullanıcı + 60 sn'lik tek kullanımlık token. |
| Posta kutusu | Yalnız alıcının kendi normalize e-postası (RLS). |
| Log/iz | Yapısal log; istek gövdesi, e-posta, telefon, ad, token loglanmaz. Kullanıcı/şirket kimliği log alanı olabilir. İstek logu (`Kulvar.Request`) yalnız yöntem, eşleşen uç **şablonu** (ham yol/sorgu değil — arama terimi ve indirme token'ı sorgu dizesindedir), durum kodu, süre ve bu iki kimliği yazar; hata yolunda da şablon korunur. Çerçevenin işlenmemiş istisna logu kapalıdır (istisna iletisi veri taşıyabilir); hatalar `traceId` ile ilişkilendirilir. |
| Metrik | Etiketler sabit kümelerdir (uç şablonu, sonuç kodu, iş türü). `tenant_id`, `user_id`, müşteri kimliği etiket değildir. |

## 7. Hata davranışı

| Durum | Davranış |
|---|---|
| Kapsamsız sorgu (kod hatası) | RLS: okuma boş, yazma hata → istek `500`, veri sızmaz |
| DB erişilemez | `503 dependency_unavailable`; `/health/ready` 503; hiçbir uç başarı döndürmez |
| Transaction ortasında hata/iptal | `ROLLBACK`; kapsam bağlantıda kalmaz; outbox/kota/idempotency kaydı da geri alınır |
| Serileştirme/deadlock | En çok 3 deneme, her biri yeni transaction + yeni kapsam |
| Worker çökmesi | Kiralama dolunca mesaj yeniden alınır; etki ve "tamamlandı" aynı transaction'dadır |
| Sağlayıcı zaman aşımı | İşlem `Unknown`; başarı/başarısızlık varsayılmaz |
| Webhook imzası geçersiz | 401, yazma yok |

## 8. Platform operatörü sınırı

- Operatör kaydı yalnız `kulvar_owner` bağlantısıyla (kurulum komutu) eklenir; `kulvar_app` rolünün `platform_operators` üzerinde yalnız `SELECT` yetkisi vardır.
  Public signup bu tabloya yazamaz (yetki yok → veritabanı reddeder).
- Operatör uçları `kulvar_platform` rolüyle çalışır: `customers`, `tickets`, `ticket_comments`, `memberships`, `invites`, `users`, `audit_events`
  tablolarında hiçbir yetki yoktur. Testler bu rolle doğrudan `SELECT` deneyip `42501` (yetki reddi) bekler.
- Impersonation/break-glass yoktur.

## 9. Sırlar

- Parolalar, bağlantı dizeleri, webhook sırrı, sertifika parolası `.local/` altında dosya olarak üretilir; git'e ve image'a girmez.
- Demo hesap parolaları `.local/seed/` altındadır; loglanmaz, belgelerde yazmaz.
- Demo seed ve simülasyon yalnız `Development`/`Testing` ortamında kabul edilir; `Production`'da açık bırakılırsa süreç başlamayı reddeder.
