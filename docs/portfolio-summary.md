# Kulvar — Portföy Özeti

**Sahibi:** TCoderr · **Tür:** çok kiracılı (multi-tenant) B2B SaaS · **Yığın:** C# / ASP.NET Core 10, EF Core 10, PostgreSQL 18, React 19 + TypeScript, Docker

Şirketlerin müşteri kayıtlarını ve destek taleplerini yönettiği bir SaaS. Projenin asıl konusu ekranlar değil, **"bir şirketin verisi asla başka şirkete sızmaz"
iddiasını kanıtlanabilir kılmak** ve para/kota/arka plan işi gibi yarış koşullarına açık yerleri doğru yapmaktır.

## Bu proje neyi gösteriyor

| Yetkinlik | Projede karşılığı | Bakılacak yer |
|---|---|---|
| Çok kiracılı veri izolasyonu | Paylaşılan şema + `tenant_id` + PostgreSQL RLS (`ENABLE` + `FORCE`, `USING` + `WITH CHECK`), işlem ömürlü kapsam, bileşik yabancı anahtarlar, EF global filtresi — üç bağımsız katman | `src/Kulvar.Platform/Data/DbRunner.cs`, `src/Kulvar.Api/Migrations/Sql/`, [tenant-isolation-verification.md](tenant-isolation-verification.md) |
| En az yetki | Dört veritabanı rolü (migration / API / platform / worker); hiçbiri superuser veya `BYPASSRLS` değil; platform operatörü içerik tablolarına **rol düzeyinde** erişemez | `db/bootstrap/`, [04-database-design.md](04-database-design.md) |
| Kimlik ve oturum güvenliği | `__Host-` cookie + sunucu tarafı oturum (her istekte doğrulanır, anında iptal), CSRF, hesap kilitleme, hız sınırı, sayım saldırısına kapalı genel yanıtlar | `src/Kulvar.Modules.Identity/`, [ADR 0003](adr/0003-auth-and-session.md) |
| Eşzamanlılık | Kota tek SQL deyimiyle atomik (son hakta 25 eşzamanlı istekten tam 1'i başarılı), iyimser eşzamanlılık (`ETag`/`If-Match`), idempotent oluşturma, "en az bir Owner" değişmezi için kilit + ertelenmiş trigger | `src/Kulvar.Modules.Billing/Entitlements.cs`, [ADR 0004](adr/0004-quota-atomicity.md) |
| Dağıtık sistem doğruluğu | Outbox/inbox, kiralama + fencing token, yeniden deneme + ölü kuyruk; imzalı webhook; yinelenen, sırasız ve geç gelen ödeme olaylarına dayanıklı durum makinesi; "zaman aşımı ≠ başarısızlık" mutabakatı | `src/Kulvar.Platform/Messaging/`, `src/Kulvar.Modules.Billing/Processing.cs`, [ADR 0005](adr/0005-billing-ordering-and-reconciliation.md), [ADR 0006](adr/0006-outbox-and-workers.md) |
| Modüler monolit | Altı modül, tek süreç imajı, dört çalışma kipi (API, worker, migrator, seed) | `src/Kulvar.Modules.*`, `src/Kulvar.Api/Program.cs` |
| Veri yaşam döngüsü | CSV dışa aktarma (formül enjeksiyonuna karşı), yetkili + kısa ömürlü + tek kullanımlık indirme, bekleme süreli şirket silme ve adım adım yinelenebilir kalıcı temizleme | `src/Kulvar.Modules.Operations/`, [ADR 0007](adr/0007-export-retention-deletion.md) |
| Test disiplini | Gerçek PostgreSQL (Testcontainers) üzerinde kısıtlı rollerle entegrasyon testleri; sahte saat ve bariyerlerle deterministik yarış testleri; migration yükseltme testleri; gerçek tarayıcı testleri | `tests/`, [09-test-and-verification-plan.md](09-test-and-verification-plan.md) |
| Operasyon | HTTPS Docker Compose yığını, salt-okunur kök dosya sistemi, yetkisiz kullanıcı, Docker secret dosyaları, sağlık uçları, kişisel veri içermeyen yapısal log | `compose.yaml`, `Dockerfile`, `scripts/` |

## Öne çıkan üç tasarım kararı

1. **İzolasyon tek bir `WHERE` koşuluna emanet değil.** Uygulama önce üyeliği doğrular, sonra işlem içinde şirket kapsamını ayarlar; bundan sonra
   yazılan her sorgu — filtre unutulsa, ham SQL yazılsa bile — RLS tarafından sınırlanır. Tablo sahibi dahi kiracı satırını göremez (`FORCE`).
   Testler katmanları ayrı ayrı devre dışı bırakıp diğerinin tek başına tuttuğunu gösterir.
2. **"Kabul edildi" ile "uygulandı" ayrıdır.** Plan değişikliği isteği `202` döner; hak (entitlement) yalnız ödeme sonucu kesinleşince ve
   işlem hâlâ güncelse (`epoch`) uygulanır. İptalden, silme isteğinden veya yeni bir istekten sonra gelen geç başarı planı yeniden açmaz, iade yoluna girer.
3. **Sayaç oku-sonra-yaz ile değil, tek deyimle tüketilir.** `INSERT … ON CONFLICT DO UPDATE … WHERE used < limit RETURNING` — yarış penceresi yok;
   idempotent tekrar ikinci kez tüketmez, başarısız işlem tüketimi geri alır.

## Nasıl doğrulandı

- Birim + entegrasyon testleri (gerçek PostgreSQL 18, kısıtlı roller), arayüz bileşen testleri, gerçek tarayıcı testleri, Docker yığın duman testi ve depo taraması.
- Sayılar, exit kodları ve koşunun kaynak parmak izi: [final-verification.md](final-verification.md).
- İzolasyonun hangi yüzeyde hangi testle sınandığı: [tenant-isolation-verification.md](tenant-isolation-verification.md).
- Geliştirme sırasında bulunan hatalar ve kök nedenleri gizlenmedi: [implementation-progress.md](implementation-progress.md).

## İki şirketle beş dakikalık tur

Demo verisi kurulduktan sonra (`up -SeedDemo -Simulation`) izolasyonu ve rol farklarını gözle görmek için:

1. `cem.ortak` ile girin. **Şirketlerim**'de iki şirket görünür: Akasya Mobilya'da *Agent*, Boreas Yazılım'da *Admin*.
2. Akasya'yı açın: 6 müşteri, 7 talep; "Ekip" ve "Denetim" yönetimi kapalı (Agent). Adres çubuğundaki şirket kimliğine dikkat edin.
3. Boreas'a geçin: şerit, ad ve veri değişir (3 müşteri, 4 talep); burada Admin olduğunuz için ekip yönetimi açıktır. Akasya'nın hiçbir kaydı görünmez.
4. Çıkın, `burak.owner` (yalnız Boreas üyesi) ile girin ve adres çubuğuna 2. adımdaki Akasya adresini yapıştırın: "şirket bulunamadı" — var olmayan bir şirketle aynı yanıt.
5. `arda.viewer` ile Akasya'ya girin: müşteri e-posta/telefonları maskelidir; yazma düğmeleri gizlenmez, nedeniyle birlikte devre dışıdır.
6. `burak.owner` ile Boreas'ta "Ekip → davet": deneme planının koltuk limiti dolu olduğu için davet formu limiti ve kullanılan koltukları gösterir, yeni davet gönderilemez. "Plan"dan Starter/Team'e geçin; `-Simulation` ile ödeme sonucunu (başarılı / reddedildi / zaman aşımı) seçebilirsiniz.
7. `operator` ile girin: `/platform` panelinde şirketlerin yalnız adı, durumu ve planı vardır; müşteri veya talep içeriğine giden bir yol yoktur. Bir şirketi askıya alın ve üyesiyle girip tam sayfa "askıda" durumunu görün.

Bu adımların otomatik karşılıkları `src/web/e2e/` altındaki tarayıcı testleridir. Tüm kişiler ve şirketler uydurmadır; proje hiçbir zaman gerçek kullanıcıya veya üretim trafiğine hizmet vermemiştir.

## Dürüst sınırlar

Ödeme sağlayıcı ve e-posta simülasyondur; yük testi yapılmadı (performans iddiası yok); uzak CI çalıştırılmadı; parola sıfırlama, MFA ve SSO yok.
Tam liste: [known-limitations.md](known-limitations.md).

## Üç komutla çalıştırma

```powershell
.\scripts\local-stack.ps1 init -SeedDemo
.\scripts\local-stack.ps1 up -SeedDemo -Simulation
# https://localhost:8843 — demo hesapları ve şirket eşlemesi README'de; parolalar .local\seed\ altında
```
