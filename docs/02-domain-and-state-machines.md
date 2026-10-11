# 02 — Alan Modeli ve Durum Makineleri

Alan kuralları saf C# kodudur (HTTP/EF/sağlayıcı bağımlılığı yok) ve birim testleriyle doğrulanır.
Zaman her yerde sunucu `TimeProvider`'ından gelir ve UTC'dir.

## 1. Varlıklar

| Modül | Varlık | Not |
|---|---|---|
| Identity | User, Session, UserToken (e-posta doğrulama), PlatformOperator | Global |
| Tenancy | Tenant, Membership, Invite, OwnershipOffer | Tenant global kayıt; diğerleri tenant'a ait |
| Customers | Customer | Tenant'a ait |
| Support | Ticket, TicketComment | Tenant'a ait |
| Billing | Plan, PlanVersion (global referans), Subscription, BillingOperation, UsageCounter, UsageEvent, ProviderEvent (inbox) | |
| Operations | OutboxMessage, SinkMessage, ExportJob, AuditEvent, PlatformAuditEvent, IdempotencyKey | |

Kimlikler UUIDv7'dir (zaman sıralı, gizli değil). Yetki hiçbir yerde kimliğin tahmin edilemezliğine dayanmaz.
Gizli değerler (oturum, davet, doğrulama, indirme token'ı) ayrı CSPRNG çıktısıdır ve yalnız SHA-256 hash'i saklanır.

## 2. Destek talebi durum makinesi

```
            start                    resolve
   Open ───────────► InProgress ───────────► Resolved ──────► Closed
    ▲ │                  │                     │   ▲   close     │
    │ └──── resolve ─────┼─────────────────────┘   │             │
    │                    │ release                 │             │
    └────────────────────┘                         │             │
    ▲                    reopen                    │             │
    └──────────────────────────────────────────────┘             │
    ▲                    reopen (≤ 30 gün, yalnız Owner/Admin)    │
    └────────────────────────────────────────────────────────────┘
```

| Geçiş | Koşul | Yan etki |
|---|---|---|
| Open → InProgress (`start`) | Atanan yoksa ve aktör Agent+ ise aktöre atanır | etkinlik |
| InProgress → Open (`release`) | — | atama kaldırılır |
| Open/InProgress → Resolved (`resolve`) | — | `resolved_at` |
| Resolved → Closed (`close`) | — | `closed_at` |
| Resolved → Open (`reopen`) | — | `reopen_count + 1`, `resolved_at` temizlenir |
| Closed → Open (`reopen`) | Yalnız Owner/Admin ve `closed_at` üzerinden ≤ 30 gün | `reopen_count + 1` |
| Diğer tüm geçişler | Reddedilir: `409 invalid_transition` | — |

Rol kuralları:
- Owner/Admin: her talepte her geçerli geçiş, her aktif üyeye atama.
- Agent: yalnız **kendine atanmış** veya **atanmamış** talepte düzenleme/geçiş/yorum; atamayı yalnız kendine alabilir veya kendinden bırakabilir.
- Viewer: yalnız okuma.
- Closed talebe yorum eklenmez (önce yeniden açılır).
- Yeniden açma kotayı etkilemez (kota "dönemde oluşturulan" talepleri sayar).

Talep numarası şirket içinde artan tamsayıdır (`#1, #2, …`), oluşturma transaction'ında şirket sayacından alınır.

## 3. Üyelik ve davet

Üyelik durumu: `Active`, `Removed`. Üyelik satırı fiziksel olarak silinmez (yorum/etkinlik yazarı ilişkisi korunur);
yeniden davet aynı satırı `Active` yapar. Fiziksel silme yalnız şirket temizlemesinde yapılır.

Davet durumu (saklanan): `Pending`, `Accepted`, `Revoked`. `Expired` saklanmaz; `expires_at <= now` ile türetilir.

```
 Pending ──accept──► Accepted
    │ ├──revoke──► Revoked
    │ └──(expires_at geçti)──► Expired (türetilmiş, kabul edilemez)
```

Davet token'ı worker tarafından posta üretilirken oluşturulur (yalnız hash'i davet satırına yazılır, ham token yalnız
demo posta kutusundaki ileti gövdesindedir). Posta üretilene kadar davet "gönderiliyor" görünür ve kabul edilemez.

E-posta normalizasyonu: `Trim` → Unicode NFKC → `ToLowerInvariant`. Büyük/küçük harf farkı aynı adrestir; artı/nokta
katlaması yapılmaz. Kültüre bağlı küçültme kullanılmaz (Türkçe I/ı tuzağı).

### Rol değişikliği kuralları

| Aktör | Hedefin mevcut rolü | Verilebilecek roller |
|---|---|---|
| Owner | Admin/Agent/Viewer | Admin, Agent, Viewer |
| Owner | Owner (başkası veya kendisi) | Admin, Agent, Viewer — yalnız başka aktif Owner kalıyorsa |
| Admin | Agent/Viewer | Agent, Viewer |
| Admin | Owner/Admin | — (403) |
| Agent/Viewer | herhangi | — (403) |

Owner rolü bu yolla **verilemez**; yalnız sahiplik teklifinin kabulüyle.

### Sahiplik teklifi

`Pending → Accepted | Cancelled | Declined`; 72 saat sonra türetilmiş `Expired`.
Kabul transaction'ı: şirket satırı kilitlenir; teklif eden hâlâ aktif Owner, hedef hâlâ aktif üye ve teklif hâlâ `Pending`
ve süresi dolmamış olmalıdır. Hedef Owner olur; `demote_self = true` ise teklif eden Admin olur.

### En az bir Owner değişmezi

Üyelik değiştiren her işlem (kabul, kaldırma, ayrılma, rol, sahiplik) önce şirket satırını `FOR NO KEY UPDATE` kilitler
(içerik yazmalarının FK kilidi `FOR KEY SHARE` ile çakışmaz; `FOR UPDATE` yalnız silme/temizleme/askıya alma gibi yaşam döngüsü değişikliklerine ayrılmıştır),
sonra aktif Owner sayısını okur. Savunma katmanı olarak ertelenmiş (deferred) bir constraint trigger commit anında
aynı kuralı veritabanında doğrular (şirket `Purging/Deleted` ise muaf).

## 4. Şirket yaşam döngüsü

```
 Active ◄──────reactivate────── Suspended (platform)
   │  └────────suspend──────────────►
   │ request deletion (Owner + onay)
   ▼
 DeletionPending ──cancel (Owner, süre içinde)──► Active
   │ purge_after geçti (worker)
   ▼
 Purging ──temizlik bitti──► Deleted (mezar taşı; geri dönüş yok)
```

## 5. Abonelik durum makinesi

```
 Trialing ──plan uygulandı──► Active ◄──ödeme alındı/plan uygulandı── PastDue
    │ trial bitti                │ yenileme başarısız                    │ ödemesiz dönem bitti
    └───────────► PastDue ◄──────┘                                       ▼
                                                                     Suspended ──plan uygulandı──► Active
 (Trialing/Active/PastDue/Suspended) ──iptal (Owner)──► Cancelled ──yeni plan işlemi başarılı──► Active
```

| Olay | Kaynak | Koruma |
|---|---|---|
| `TrialExpired` | worker zamanlayıcı | `status = Trialing` ve `trial_ends_at <= now` |
| `GraceExpired` | worker zamanlayıcı | `status = PastDue` ve `grace_ends_at <= now` |
| `PlanApplied` | başarılı ödeme işlemi | işlem `epoch` = abonelik `epoch`; şirket `Active` |
| `RenewalFailed` / `Renewed` | sağlayıcı webhook | olay `sequence` > `provider_sequence` |
| `Cancelled` | Owner | `epoch + 1` (eski işlemlerin geç başarısı artık uygulanamaz) |

`epoch`: her yeni plan işlemi isteğinde, iptalde ve şirket silme isteğinde artar; yani yalnız **en son istenen** işlem uygulanabilir.
Bir ödeme işlemi istendiği anda oluşan `epoch`'u taşır; başarı sonucu yalnız
epoch eşleşirse entitlement'a uygulanır. Eşleşmezse başarı "sahipsiz"dir ve telafi (iade) yoluna girer.

### Erişim modu (şirket durumu × abonelik durumu)

| Şirket | Abonelik | Mod | Anlamı |
|---|---|---|---|
| Active | Trialing / Active / PastDue | `Full` | Her şey role göre açık. PastDue'da uyarı bandı. |
| Active | Suspended / Cancelled | `Recovery` | İçerik salt-okunur. Owner plan seçebilir, üye kaldırabilir, planı izin veriyorsa export alabilir. Davet yok. |
| DeletionPending | herhangi | `ReadOnly` | İçerik salt-okunur. Owner silmeyi iptal edebilir, planı izin veriyorsa export alabilir. |
| Suspended (platform) | herhangi | `Locked` | Yalnız çalışma alanı özeti (durum bilgisi) ve şirketten ayrılma. |
| Purging / Deleted | — | yok | 404. |

Login hiçbir modda engellenmez; kullanıcı diğer şirketlerine girebilir.

## 6. Ödeme işlemi (BillingOperation) durum makinesi

```
 Pending ──sağlayıcı: başarılı──► Succeeded (entitlement uygulandı)            [terminal]
    │ ├──sağlayıcı: ret──► Declined                                           [terminal]
    │ ├──geçici hata──► Pending (sınırlı yeniden deneme, aynı merchant referansı)
    │ └──zaman aşımı──► Unknown ──sorgu: başarılı──► Succeeded | Orphaned yolu
    │                      │ ├──sorgu: ret──► Declined
    │                      │ └──sorgu sınırı aşıldı──► NeedsResolution
    │
    └──başarı ama epoch/şirket uyumsuz──► RefundPending ──iade başarılı──► Refunded   [terminal]
                                              └──iade başarısız (sınır)──► NeedsResolution
 NeedsResolution ──operatör: iade──► RefundPending (worker iade eder ──► Refunded) | ──operatör: void──► Voided | ──operatör: elle kapat──► ManuallyClosed  [terminal]
```

Kurallar:
- Terminal durum değişmez; aynı sonuç tekrar gelirse etkisizdir, farklı sonuç gelirse olay `Conflict` olarak kaydedilir.
- Bir şirkette aynı anda en çok bir **sonucu bekleyen** işlem (`Pending/Unknown`) vardır (kısmi unique indeks). İade bekleyen (`RefundPending`)
  eski bir işlem yeni plan isteğini engellemez.
- "Kabul edildi" (202) ≠ "uygulandı". Reddedilen veya sonucu bilinmeyen işlem entitlement'ı yükseltmez.
- Zaman aşımı başarısızlık değildir: işlem `Unknown` olur ve mutabakat sorgusuyla çözülür.
- Operasyon kuyruğuna düşmek telafi sayılmaz; `NeedsResolution` ancak operatör eylemiyle (izin + gerekçe + denetim kaydı) terminal olur.

## 7. Export işi

`Queued → Running → Ready → Expired`; hata: `Failed`; şirket silinirken: `Cancelled`.
Artefakt `Ready`'den 24 saat sonra silinir. İndirme token'ı 60 saniye geçerli ve tek kullanımlıktır.

## 8. Outbox mesajı

`Pending → (kiralandı) → Done | Pending (yeniden deneme, üstel bekleme + jitter) | Dead (deneme sınırı)`.
`Dead` mesaj operatör tarafından yeniden `Pending` yapılabilir. Kiralama süresi dolan mesaj başka worker tarafından alınır;
eski worker'ın tamamlama denemesi `lease_token` uyuşmadığı için etkisiz kalır (fencing).
