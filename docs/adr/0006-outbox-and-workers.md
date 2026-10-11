# ADR-0006 — Outbox, inbox ve worker'lar

Durum: Kabul edildi (2026-10-10)

## Bağlam
İş etkisi ile yan etki (posta, sağlayıcı çağrısı, export) arasında tutarlılık gerekir. Süreç her an çökebilir; teslimat en az bir kezdir.

## Karar
- **Outbox**: iş etkisi ve `outbox_messages` satırı aynı transaction'da. Uygulama rolü yalnız ekleyebilir (okuyamaz).
- **Kiralama**: worker partiyi `FOR UPDATE SKIP LOCKED` ile alır; `lease_owner`, `lease_until`, `lease_token = lease_token + 1`, `attempts + 1` yazar.
- **Fencing**: tamamlama `WHERE id = @id AND lease_token = @token` ile yapılır. Etki ve tamamlama aynı transaction'dadır; 0 satır etkilenirse
  transaction geri alınır. Böylece kiralamasını kaybetmiş worker'ın etkisi kalıcı olmaz.
- **Etkiler idempotent**: DB içi etkiler (posta kutusu iletisi) benzersiz `dedupe_key` taşır. Dış etki (sağlayıcı) `merchant_reference` ile idempotenttir.
- **Yeniden deneme**: üstel bekleme (taban 5 sn, tavan 10 dk) + %20 jitter; 8 denemeden sonra `Dead`. Operatör yeniden kuyruğa alabilir.
- **Adillik**: parti en çok 20 mesaj, şirket başına en çok 5 (pencere fonksiyonu ile aday seçimi, sonra kilitleme).
- **Kapsam**: worker her mesajda şirket durumunu okur; `Purging/Deleted` şirketin mesajı etkisiz kapatılır. Kaynak, kurulan kapsamda aranır;
  mesaj gövdesindeki kimlikler yetki üretmez.
- **Kapatma**: `CancellationToken` mesajlar arasında kontrol edilir; yarım kalan iş kiralama dolunca yeniden alınır.
- **Saat**: tüm süreler sunucu `TimeProvider`'ından; testlerde sahte saat.

Token üretimi: davet ve e-posta doğrulama token'ları posta üretilirken **worker'da** oluşturulur; hash'i ilgili satıra, ham değer yalnız
posta kutusu iletisine aynı transaction'da yazılır. Yeniden teslimatta ileti tekildir (`dedupe_key`), token değişmez.

## "Tam bir kez" iddiası yok
Demo posta kutusu aynı veritabanında olduğu için ileti + tamamlama atomiktir ve yineleme üretmez. Gerçek bir e-posta sağlayıcısında
teslimat en az bir kezdir; bu ürün dış e-posta için "exactly once" iddia etmez.

## Alternatifler
- Mesaj kuyruğu (RabbitMQ/Kafka): ayrı altyapı ve çift yazma sorunu; bu hacimde gereksiz. Reddedildi.
- `LISTEN/NOTIFY`: gecikmeyi azaltır; kalıcılık yine tablo ister. Şimdilik 1 sn poll yeterli.
- Hangfire/Quartz: bağımlılık ve kendi şeması; kiralama mantığını gizler. Reddedildi.

## Sınırlar
Tek worker örneğiyle doğrulanır; çoklu örnek davranışı kiralama/fencing testleriyle (iki mantıksal worker) sınanır ama gerçek çoklu süreç yük testi yapılmamıştır.
