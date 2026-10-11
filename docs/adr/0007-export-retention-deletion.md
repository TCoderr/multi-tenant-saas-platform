# ADR-0007 — Export, saklama ve şirket silme

Durum: Kabul edildi (2026-10-10)

## Export
- Async iş: `export_jobs` satırı isteyen kullanıcı ve şirketle oluşturulur; worker şirket kapsamında müşteri/talep/yorum okur.
- Biçim: ZIP içinde `customers.csv`, `tickets.csv`, `comments.csv`. CSV: UTF-8 (BOM'lu), ayırıcı `,`, satır sonu CRLF, RFC 4180 tırnaklama.
- Formül enjeksiyonu: `=`, `+`, `-`, `@`, sekme veya satır başı ile başlayan hücrelerin başına `'` eklenir.
- Depolama: uygulamanın özel kökü (`Exports:Root`), yol yalnız sunucu üretimi GUID'lerden: `<kök>/<tenantId>/<jobId>.zip`.
  İstemci girdisi yola hiç girmez; çözümlenen yol kökün altında değilse istek reddedilir.
- İndirme: oturum + güncel üyelik + `export.manage` + işi isteyen kullanıcı + 60 saniyelik tek kullanımlık token (hash'i saklanır). Her indirme denetim kaydına yazılır.
- Ömür: `Ready` olduktan 24 saat sonra dosya silinir, iş `Expired` olur. Süreler yapılandırılabilir.

## Şirket silme
1. Owner ister; gövdede şirket adı ve parolası doğrulanır. Şirket `DeletionPending` olur, `purge_after = now + 7 gün`, abonelik `epoch` artar. Yazma durur.
2. Süre içinde Owner iptal edebilir → `Active`.
3. Süre dolunca worker şirketi `Purging` yapar ve belgeli sırayla (04 §8) şirket kapsamında siler. Her adım tekrar çalıştırılabilir; yarıda kalan temizleme kaldığı yerden sürer.
4. Son adım: export dizini silinir, şirket satırı mezar taşına dönüşür (`Deleted`, ad temizlenir).

Yarışlar:
- **Bekleyen işler**: worker her işten önce şirket durumunu okur; `Purging/Deleted` şirketin işi etkisiz kapanır ve veri üretmez.
- **Export**: export tamamlama transaction'ı şirket satırını `FOR SHARE` kilitleyip durumu doğrular; şirket `Purging/Deleted` ise dosyasını siler.
  Temizleme durumu `FOR UPDATE` ile değiştirir. Silinmiş şirketin indirme ucu 404'tür.
- **Davet/idempotency/oturum**: şirket 404 olduğu için hiçbiri kullanılamaz; satırları temizlenir.
- **Geç ödeme başarısı**: epoch uyuşmaz → iade yolu (ADR-0005).

Paylaşılan kimlik: `users`/`sessions` global olduğundan silinmez; temizleme şirket kapsamında çalıştığı için başka şirketlerin üyelikleri RLS gereği görünmez bile.

## Saklama
| Veri | Süre (demo varsayılanı, yapılandırılabilir) |
|---|---|
| Export artefaktı | 24 saat |
| İndirme token'ı | 60 saniye |
| Idempotency kaydı | 24 saat |
| Silme bekleme penceresi | 7 gün |
| Mezar taşı, ödeme işlemleri, platform denetimi | Süresiz (kişisel veri içermez) |
| Şirket denetim kaydı | Şirket silinene kadar |

## Sınırlar
- Yedekten geri yükleme sonrası silmenin yeniden uygulanması üretim politikası olarak doğrulanmamıştır.
- Süreler demo varsayımıdır; KVKK/GDPR uyumu iddiası yoktur.
- Kullanıcı hesabı silme ve kişisel veri erişim talebi kapsam dışıdır.
