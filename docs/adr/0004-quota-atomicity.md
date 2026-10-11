# ADR-0004 — Kota ve koltuk atomikliği

Durum: Kabul edildi (2026-10-10)

## Bağlam
"Önce say, sonra ekle" kalıbı eşzamanlı isteklerde limiti aşar. Kota aynı zamanda idempotent tekrar, rollback ve plan değişikliği yarışında doğru kalmalıdır.

## Karar

### Aylık talep kotası
- Sayaç satırı: `usage_counters(tenant_id, metric, period_start)`. Tüketim tek deyimdir:
  `INSERT … ON CONFLICT … DO UPDATE SET used = used + 1 WHERE used < @limit RETURNING used`. Satır dönmezse kota doludur.
- Tüketim, talep ekleme, etkinlik, outbox ve idempotency kaydı **aynı transaction**'dadır. Rollback hepsini birlikte geri alır.
- Idempotent tekrar, sayaç deyimine hiç ulaşmaz (kayıtlı yanıt döner).
- `@limit`, aynı transaction'da `subscriptions` satırı `FOR SHARE` kilitlenerek okunur. Plan değişikliği uygulaması satırı `FOR UPDATE` alır:
  bir oluşturma ya değişiklikten önce commit olur (eski limit) ya da sonra başlar (yeni limit). Arada kalan durum yoktur.
- Dönem: **UTC takvim ayı**; `period_start = date_trunc('month', now at UTC)`. Yerel saat/DST etkilemez.
- Değişmez kayıt: `usage_events(tenant_id, metric, source_id)` yalnız eklenir. Talebi kapatmak/yeniden açmak sayaç veya kayıt değiştirmez;
  talepler tek tek silinemez (yalnız şirket temizlemesi).

### Koltuk
- Aktif üyelikler sayılır; bekleyen davet koltuk **ayırır**.
- Davet oluşturma ve kabul, şirket satırını `FOR NO KEY UPDATE` kilitleyerek seri hale gelir; sayım kilit altında yapılır.
- Kabulde `aktif üye < limit` yeniden doğrulanır (arada downgrade olmuş olabilir).
- Downgrade veri silmez; limit üstündeki şirkette yeni davet/kabul reddedilir.

### Yanıt
`403` + `code: quota_exceeded`, `metric`, `limit`, `used`, `resetsAt` (koltukta `resetsAt` yok). `429` yalnız taşıma hız sınırıdır.

## Alternatifler
- `SELECT count(*)` + ekleme: yarışa açık. Reddedildi.
- `SERIALIZABLE` yalıtımı: doğru ama yeniden deneme fırtınası ve tüm yolun buna göre yazılması gerekir. Reddedildi.
- Danışma kilidi (advisory lock): çalışır; satır kilidi aynı işi veriyle birlikte görünür kılıyor. Reddedildi.
- Redis sayaç: DB transaction'ı ile atomik değil. Reddedildi.

## Sonuçlar
Şirket başına sayaç satırı sıcak noktadır; aynı şirketin eşzamanlı talep oluşturmaları bu satırda sıraya girer. Ölçülmedi; verim iddiası yoktur.
