# ADR-0005 — Faturalama sırası, idempotency ve mutabakat

Durum: Kabul edildi (2026-10-10)

## Bağlam
Sağlayıcı çağrısı ağ üzerindedir: yanıt kaybolabilir, webhook yinelenebilir, sırasız veya çok geç gelebilir.
Gerçek sağlayıcı yoktur; `IBillingProvider` arkasında durumunu `fakepsp` şemasında tutan simüle sağlayıcı vardır.

## Karar

1. **İşlem kaydı önce**: plan değişikliği isteği, sağlayıcıya gitmeden önce `billing_operations` satırı (rastgele `merchant_reference`, o anki abonelik `epoch`'u)
   ve outbox mesajını tek transaction'da yazar. Yanıt `202`'dir; entitlement değişmez.
2. **Sağlayıcı idempotency'si**: her çağrı aynı `merchant_reference` ile yapılır. Yeniden deneme yeni ücret üretmez.
3. **Zaman aşımı ≠ başarısızlık**: işlem `Unknown` olur. Mutabakat sağlayıcıyı `merchant_reference` ile sorgular;
   "bulunamadı" ise aynı referansla yeniden gönderir. Sınır aşılırsa `NeedsResolution`.
4. **Tek uygulama noktası**: başarı ister eşzamanlı yanıttan, ister webhook'tan, ister mutabakattan gelsin aynı fonksiyon uygular.
   Fonksiyon işlemi satır kilidiyle alır; terminal durum değişmez (ikinci başarı etkisizdir).
5. **Epoch koruması**: başarı yalnız `operation.epoch = subscription.epoch` ve şirket `Active` ise entitlement'a uygulanır.
   Her yeni plan işlemi isteği, iptal ve silme isteği epoch'u artırır (yalnız en son istenen işlem uygulanabilir). Aksi halde başarı "sahipsiz"dir: işlem `RefundPending` olur, iade denenir; olmazsa `NeedsResolution`.
6. **Webhook güvenliği**: ham gövde baytları üzerinde HMAC-SHA256, `t=` zaman damgası (±5 dk), 64 KiB sınır. Olay kimliği birincil anahtardır;
   aynı kimlik + farklı gövde hash'i `Conflict`.
7. **Yönlendirme**: şirket, bizim kayıtlarımızdaki `merchant_reference` veya `provider_subscription_id` eşleşmesinden bulunur. Gövdedeki herhangi bir şirket alanı kullanılmaz.
8. **Sıra**: abonelik olayları sağlayıcının `sequence` değeriyle sıralanır; yalnız daha büyük sıra uygulanır. Zaman damgasına göre "en yeni kazanır" yapılmaz.
9. **Telafi terminaldir**: operasyon kuyruğuna düşmek çözüm değildir. `NeedsResolution` yalnız operatörün iade/iptal/elle kapatma eylemiyle
   (gerekçe + platform denetim kaydı) terminal olur.

## Alım ve işleme ayrımı
API (`kulvar_platform`) imzayı doğrular ve olayı inbox'a yazar. Worker (`kulvar_worker`) her olayı tek transaction'da uygular:
inbox durumu + abonelik/işlem etkisi + bildirim outbox'ı. Aynı olay iki kez işlenmeye çalışılırsa satır kilidi ve durum kontrolü ikinciyi etkisiz kılar.

## Alternatifler
- Webhook'u istek içinde uygulamak: daha az gecikme; API rolüne şirketler arası abonelik yazma yetkisi gerekir. Reddedildi.
- Yalnız eşzamanlı yanıta güvenmek: yanıt kaybında para/entitlement ayrışır. Reddedildi.
- Zaman damgasıyla sıralama: saat kayması ve geç teslimde eski durum geri gelebilir. Reddedildi.

## Sınırlar
Kısmi dönem ücreti, vergi, fatura, kart saklama, 3DS, gerçek iade akışı yoktur. Yenileme (renewal) sağlayıcı tarafından başlatılan olaylarla simüle edilir.
