# 01 — Ürün Gereksinimleri

Ürün adı: **Kulvar** — şirketlerin müşteri kayıtlarını ve destek taleplerini yönettiği çok kiracılı (multi-tenant) B2B SaaS.
Ad, yüzme/atletizm kulvarından gelir: her şirket kendi kulvarında çalışır, kulvarlar birbirine karışmaz.
Sahip/yazar: TCoderr. Demo ürünüdür; gerçek müşteri, gerçek ödeme, gerçek e-posta yoktur.

## 1. Problem ve hedef kullanıcı

Küçük/orta ölçekli şirketlerin destek ekipleri müşteri kayıtlarını ve talepleri tek yerde tutmak ister.
Aynı kişi (ör. serbest çalışan destek uzmanı) birden fazla şirkette farklı rolle çalışabilir.
Ürünün asıl mühendislik iddiası: **bir şirketin verisi başka şirkete hiçbir yüzeyden sızmaz** ve bu iddia gerçek
PostgreSQL üzerinde, kısıtlı runtime rolüyle test edilir.

## 2. Kavramlar

| Kavram | Tanım |
|---|---|
| Şirket (tenant) | Verinin, planın ve üyeliklerin izolasyon birimi. |
| Kullanıcı | Global kimlik (e-posta + parola). Birden çok şirkete üye olabilir. |
| Üyelik | (şirket, kullanıcı, rol). Rol şirkete özeldir. |
| Platform operatörü | Uygulama genelinde şirket yaşam döngüsü ve operasyon kuyruğu; şirket içeriğini okuyamaz. |
| Plan / abonelik | Şirketin limit ve özellik seti (entitlement). Simüle sağlayıcı ile değişir. |

## 3. Roller

| Rol | Özet |
|---|---|
| Owner | Şirket ayarları, üyelik/rol, plan, export, sahiplik teklifi, şirket silme. |
| Admin | Müşteri/talep yönetimi; yalnız Agent/Viewer davet/rol/kaldırma; denetim kaydı okuma. Plan yönetimi, export, sahiplik yok. |
| Agent | Müşteri ve talep işleri; kendi üzerindeki veya atanmamış talepler. Plan/rol yönetimi yok. |
| Viewer | Salt-okunur. Müşteri e-posta/telefonu maskeli; export ve denetim kaydı yok. |
| PlatformOperator | Şirket metadata'sı, askıya alma/geri açma, çözüm bekleyen ödeme işlemleri, ölü işler. Şirket içeriği yok, impersonation yok. |

Ayrıntılı izin matrisi: [06-security-and-tenant-isolation.md](06-security-and-tenant-isolation.md) §3.

Kurallar:
- Public signup yalnız normal kullanıcı oluşturur; platform rolü yalnız kurulum komutuyla (migration rolü) verilir.
- Bir şirkette en az bir aktif Owner her zaman vardır (kaldırma, ayrılma, rol düşürme ve devir yarışlarında da).
- Owner rolü yalnız hedef üyenin **açık kabulü** ile verilir (sahiplik teklifi). Rol değiştirme veya davet ile Owner atanamaz.
- Admin; Owner veya başka Admin üzerinde işlem yapamaz, kimseyi Admin/Owner yapamaz (self-escalation yok).

## 4. İşlevsel gereksinimler

### 4.1 Kimlik
- F-ID-1 Signup (e-posta, görünen ad, parola ≥ 12 karakter). Yanıt her durumda aynıdır (hesap var/yok sızdırmaz).
- F-ID-2 E-posta doğrulama: doğrulama bağlantısı demo posta kutusuna düşer. Doğrulanmamış kullanıcı giriş yapabilir ama şirket oluşturamaz/davet kabul edemez.
- F-ID-3 Login/logout. Oturum sunucu tarafında tutulur; logout sonrası kopyalanmış cookie reddedilir.
- F-ID-4 Hatalı giriş kilidi (5 deneme → 15 dk) ve IP başına istek sınırı; yanıt geneldir.

### 4.2 Şirket ve üyelik
- F-TN-1 Doğrulanmış kullanıcı şirket oluşturur → Owner üyeliği + 14 günlük Trial aboneliği aynı transaction'da.
- F-TN-2 Kullanıcı üyeliklerini listeler ve çalışma alanına girer. Şirket bağlamı URL'dedir; paralel sekmeler bağımsızdır.
- F-TN-3 Davet: e-posta + rol (Admin/Agent/Viewer). Token CSPRNG, yalnız hash saklanır, 7 gün geçerli, tek kullanımlık, iptal edilebilir.
- F-TN-4 Davet kabulü: giriş yapmış, e-postası doğrulanmış ve davet e-postasıyla eşleşen kullanıcı kabul eder. Koltuk limiti kabulde yeniden kontrol edilir.
- F-TN-5 Rol değişikliği, üyelik kaldırma, şirketten ayrılma.
- F-TN-6 Sahiplik teklifi: Owner → aktif üye; hedef kabul edince Owner olur; teklif "devret" modundaysa teklif eden aynı transaction'da Admin'e düşer.

### 4.3 Müşteriler
- F-CU-1 Müşteri: ad, şirket unvanı, e-posta, telefon, not. Oluştur/güncelle/listele/ara/detay.
- F-CU-2 Silme yalnız Owner/Admin ve talebi olmayan müşteri için.
- F-CU-3 Güncelleme `If-Match` ister; eski sürüm 412.

### 4.4 Destek talepleri
- F-TK-1 Talep: müşteri, başlık, açıklama, öncelik (Low/Normal/High/Urgent), atanan üye, durum.
- F-TK-2 Durumlar: Open → InProgress → Resolved → Closed; yeniden açma kuralları [02](02-domain-and-state-machines.md) §2.
- F-TK-3 Oluşturma `Idempotency-Key` ister ve aylık talep kotasını aynı transaction'da tüketir.
- F-TK-4 Düz metin yorumlar; HTML yorumlanmaz. Dosya eki, public bağlantı yok.
- F-TK-5 Talep zaman çizelgesi (yorum + etkinlik) ve şirket denetim kaydı.

### 4.5 Plan, kota ve abonelik
- F-BL-1 Planlar ve limitler (aşağıdaki tablo). Limit kaynağı sunucudaki abonelik + plan sürümü anlık görüntüsüdür.
- F-BL-2 Plan değişikliği: Owner ister (202 "kabul edildi"); entitlement ancak sağlayıcı başarı sonucu uygulanınca değişir.
- F-BL-3 Simüle sağlayıcı: başarı, ret, geçici hata, zaman aşımı (sonuç bilinmiyor), yinelenen/sırasız/geç webhook.
- F-BL-4 Trial bitişi → PastDue (7 gün ödemesiz dönem) → Suspended. Kurtarma: Owner plan seçer.
- F-BL-5 Çözülemeyen ödeme sonucu operatör kuyruğuna düşer ve iade/iptal/elle kapatma ile terminal olur.

| Plan | Fiyat (TRY/ay, kuruş) | Koltuk | Aylık talep | Export |
|---|---|---|---|---|
| Trial (14 gün) | 0 | 3 | 20 | Hayır |
| Starter | 49 000 | 5 | 200 | Evet |
| Team | 149 000 | 25 | 2 000 | Evet |

Demo varsayımları: tek para birimi TRY, kuruş cinsinden tamsayı; vergi/komisyon/fatura/kısmi dönem (proration) yok.

Politikalar:
- **Koltuk**: aktif üyelikler sayılır. Bekleyen (süresi dolmamış, iptal edilmemiş) davet de koltuk ayırır: `aktif üye + bekleyen davet < koltuk limiti` ise yeni davet verilir. Kabul anında `aktif üye < koltuk limiti` yeniden doğrulanır.
- **Downgrade**: hemen uygulanır, hiçbir veri silinmez. Mevcut üye sayısı yeni limitin üstündeyse üyeler kalır, okunabilirlik değişmez; yeni davet ve kabul limit altına inilene kadar reddedilir.
- **Aylık talep kotası**: UTC takvim ayı. "Dönemde oluşturulan" değişmez sayaç + kayıt defteri; talebi kapatmak/yeniden açmak kotayı geri vermez.
- **Kota aşımı**: `403` + `quota_exceeded` (limit, kullanılan, sıfırlanma zamanı). Bu, taşıma katmanı sınırı olan `429` ile karıştırılmaz.

### 4.6 Arka plan işleri
- F-OP-1 Outbox: iş etkisi ve mesaj aynı transaction'da. Kiralamalı (lease) worker, sınırlı yeniden deneme, ölü mektup, yeniden oynatma.
- F-OP-2 Demo posta kutusu (mail sink): davet, doğrulama, plan bildirimleri. Yalnız alıcı kullanıcı kendi iletilerini görür.
- F-OP-3 Mutabakat (reconciliation): sonucu bilinmeyen ödeme işlemleri sağlayıcıdan sorgulanır.

### 4.7 Export, saklama ve silme
- F-EX-1 Owner async export ister (müşteriler, talepler, yorumlar; CSV içeren ZIP). Artefakt 24 saat sonra silinir.
- F-EX-2 İndirme: oturum + üyelik + izin + kısa ömürlü tek kullanımlık indirme token'ı.
- F-DL-1 Şirket silme: Owner isteği + onay (şirket adı + parola) → DeletionPending (yazma durur) → 7 gün içinde iptal edilebilir → arka planda kalıcı temizleme → mezar taşı (tombstone).

### 4.8 Platform operatörü
- F-PL-1 Şirket metadata listesi (ad, durum, plan, abonelik durumu, tarihleri).
- F-PL-2 Şirketi askıya alma / geri açma (gerekçe zorunlu, denetim kaydı).
- F-PL-3 Çözüm bekleyen ödeme işlemleri ve ölü işler; çözüm/yeniden oynatma.

## 5. İşlevsel olmayan gereksinimler

- N-1 Şirket izolasyonu uygulama (yetki + EF filtresi) ve veritabanı (RLS + bileşik FK) katmanlarında ayrı ayrı test edilir.
- N-2 Runtime DB rolleri şema sahibi/superuser/BYPASSRLS değildir.
- N-3 Hata yanıtları `application/problem+json`; login yönlendirmesi yok.
- N-4 Log/iz/metrikte müşteri kişisel verisi yok; metrik etiketlerinde tenant/müşteri kimliği yok.
- N-5 Arayüz 360/768/1440 px genişlikte kullanılabilir; klavye ile gezilebilir; kontrast ve azaltılmış hareket desteklenir.
- N-6 Yerel HTTPS Docker yığını; kilit dosyalarıyla tekrarlanabilir kurulum.

## 6. Kapsam dışı

Gerçek ödeme sağlayıcı, gerçek e-posta/SMS, SAML/merkezi OIDC, MFA, parola sıfırlama, hesap silme, özel alan adı, SCIM,
CRM pipeline, ERP, bilgi bankası/AI, dosya eki, Kubernetes, şirket başına otomatik DB/bulut kaynağı, veri ambarı,
üretim yük/performans iddiası, destek için impersonation/break-glass, gerçek zamanlı (SignalR) güncelleme.

## 7. Mevcut envanter değerlendirmesi

Yerelde iki komşu proje görüldü (yalnız okundu, değiştirilmedi): etkinlik/bilet platformu (compose projesi `loca`, port 8643) ve
lojistik takip sistemi (compose projesi `lst`, port 8443). İkisi de bu ürünle aynı değildir; organizatör sahipliği veya login
içermeleri bağımsız bir multi-tenant SaaS ürününün var olduğunu göstermez. Public (uzak) envantere erişim denenmedi; değerlendirme
yerel disk ve çalışan Docker kaynaklarıyla sınırlıdır. Bu ürün ayrı dizinde, ayrı compose proje adı (`kulvar`) ve ayrı port (8843) ile kurulur.
