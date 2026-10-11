# ADR-0001 — Kiracı depolama modeli

Durum: Kabul edildi (2026-10-10)

## Bağlam
Şirket verisi birbirinden ayrılmalı; ürün tek ekip tarafından, tek PostgreSQL ile, yerelde çalıştırılıyor.
Aynı kullanıcı birden çok şirkete üye olabiliyor (kimlik global).

## Seçenekler

| Ölçüt | A) Paylaşılan şema + `tenant_id` | B) Şirket başına şema | C) Şirket başına veritabanı |
|---|---|---|---|
| Migration | Tek kez | Şema sayısı kadar; yarım kalan sürüm riski | DB sayısı kadar; orkestrasyon gerekir |
| Bağlantı havuzu | Tek havuz | Tek havuz + `search_path` yönetimi (yanlış ayar = sızıntı) | Şirket başına havuz veya pgbouncer; bağlantı sayısı patlar |
| Yedek/geri yükleme | Tümü birlikte; tek şirket geri yükleme zor | Şema bazında mümkün ama elle | Şirket bazında doğal |
| İzolasyon gücü | Mantıksal: filtre + RLS + bileşik FK | Şema sınırı + yetki | En güçlü (fiziksel) |
| Şirketler arası sorgu (operasyon, kuyruk) | Kolay | UNION/döngü | Uygulama katmanında toplama |
| Global kimlikle ilişki (üyelik) | Doğal FK | Şemalar arası FK | FK yok; tutarlılık uygulamada |
| Maliyet / operasyon | En düşük | Orta (katalog şişmesi, araç desteği) | En yüksek (provisioning, izleme, sürüm kayması) |
| Gürültülü komşu | Paylaşılan kaynak | Paylaşılan kaynak | Ayrılabilir |

## Karar
**A**: tek veritabanı, paylaşılan şema, her kiracı tablosunda `tenant_id NOT NULL`, bileşik FK'ler ve PostgreSQL RLS.

## Gerekçe
- Ürünün ihtiyacı mantıksal izolasyonun **kanıtlanabilir** olmasıdır; A bunu tek migration ve tek havuzla sağlar ve testlerde doğrudan sınanabilir.
- B, "daha kurumsal" görünse de bu ölçekte yalnız operasyon yükü getirir: şirket sayısı kadar şema migration'ı ve `search_path` hatası riski.
- C, şirket başına provisioning, yedek ve sürüm yönetimi ister; kapsam dışıdır (otomatik DB/bulut kaynağı yok).

## Sonuçlar
- Her sorgu yolu şirket kapsamını taşımak zorunda; bunu çalıştırıcı + EF filtresi + RLS üç ayrı katmanda zorlar (ADR-0002).
- Tek şirketin zaman içinde geri yüklenmesi ve fiziksel veri yerleşimi (data residency) desteklenmez.
- Yedekten geri yükleme sonrası silinmiş şirketlerin yeniden temizlenmesi üretim politikası olarak doğrulanmamıştır.

## Ne zaman değişir
Sözleşmeyle ayrı veritabanı/bölge isteyen müşteri, tek şirketin diğerlerini ölçülebilir biçimde etkilemesi veya şirket bazlı yedek/geri yükleme
zorunluluğu. O durumda `tenants` kaydına yerleşim bilgisi eklenip bağlantı çözümleyici şirket bazlı hale getirilir (C'ye kademeli geçiş).
