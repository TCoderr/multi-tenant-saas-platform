# ADR-0002 — İzolasyon savunma katmanları ve RLS tasarımı

Durum: Kabul edildi (2026-10-10)

## Bağlam
Paylaşılan şemada (ADR-0001) tek bir unutulmuş `WHERE` sızıntı demektir. Uygulama filtresi tek başına yeterli güvence değildir:
`IgnoreQueryFilters`, ham SQL, `ExecuteUpdate/Delete` ve arka plan işleri filtreyi atlayabilir.

## Karar
1. **Yetki**: her şirket kapsamlı istek `TenantExecutor`'dan geçer; üyelik/rol/erişim modu her istekte DB'den okunur.
2. **EF global sorgu filtresi**: `ITenantOwned` varlıklarda `tenant_id = kapsam`. Kapsam context ömrü boyunca sabittir.
3. **PostgreSQL RLS**: tüm kiracı ve kimlik tablolarında `ENABLE + FORCE`; politikalar `USING` ve `WITH CHECK` içerir.
4. **Bileşik FK ve kapsamlı unique**: şirketler arası bağlama veritabanında imkânsızdır.
5. **Rol ayrımı**: `kulvar_owner` (migration), `kulvar_app`, `kulvar_platform`, `kulvar_worker`. Hiçbir runtime rolü sahip, superuser veya `BYPASSRLS` değildir.

Kapsam, transaction-yerel `set_config('app.*', value, true)` ile **parametreli** ayarlanır (dize birleştirme yok). Commit/rollback ile kaybolur;
havuza dönen bağlantıda kapsam kalmaz. Okumalar dahil her iş birimi açık transaction içindedir; yeniden denemede kapsam baştan kurulur.

## Ayrıntılı kararlar ve bedelleri

| Konu | Karar | Bedel / sınır |
|---|---|---|
| Sahip atlaması | `FORCE ROW LEVEL SECURITY`; sahibe politika yok | Migration rolü kiracı satırı okuyamaz. Veri taşıyan migration gerekirse o migration'a özel, dar ve aynı migration'da kaldırılan politika yazılır |
| SECURITY DEFINER | Kullanılmıyor | Login/davet/oturum araması için "arama anahtarı kapsamı" (`app.login_email`, `app.invite_token_hash`) kullanılır; ayrıcalıklı fonksiyon ve `search_path` kaçırma yüzeyi yok. Bedeli: fazladan kapsam anahtarları |
| Yardımcı fonksiyonlar | `LANGUAGE sql STABLE`, `SET search_path = pg_catalog` | — |
| Döngüsel bağımlılık | `memberships`/`tenants` kullanıcı kapsamıyla da okunabilir; içerik tabloları okunamaz | Kullanıcı kendi üyeliklerinin metadata'sını şirket kapsamı olmadan görür (istenen davranış) |
| Kapsam "üyelikten sonra" | İki istisna: şirket oluşturma (sunucu üretimi id) ve davet kabulü (token ile doğrulanmış davetin tenant'ı) | Bu iki yol ayrıca test edilir |
| Platform operatörü | Ayrı DB rolü; içerik tablolarında yetki yok | API süreci iki bağlantı dizesi taşır |
| Ayrıcalıklı worker | `BYPASSRLS` **yok**. Kuyruk/metadata tablolarında `USING (true)` politikası; içerik için şirket kapsamı ayarlar | Worker her iş için şirket durumunu okuyup kapsam kurar; şirketler arası içerik sorgusu yazılamaz (export/temizleme şirket şirket ilerler) |
| Şirketler arası operasyon | RLS geçici kapatılmaz, runtime rolü yükseltilmez | — |
| Trigger | "En az bir Owner" ertelenmiş constraint trigger'ı çağıran yetkisiyle çalışır | Kapsamsız çağrıda hata verir (fail closed) |
| DbContext havuzu | Kullanılmıyor | Küçük ayırma maliyeti; context yeniden kullanımıyla kapsam taşınması riski sıfır |

## Güven sınırı (dürüst)
RLS yetkilendirmenin yerine geçmez ve **ele geçirilmiş runtime'a karşı koruma değildir**: `kulvar_app` ile keyfî SQL çalıştırabilen biri
`set_config('app.tenant_id', …)` ile kapsam değiştirebilir. RLS; unutulmuş filtre, yanlış bağlanmış kimlik, kapsamsız iş gibi **uygulama hatalarını**
veri sızıntısına dönüşmeden durdurur. Ele geçirilmiş süreç senaryosu için ek sınır (imzalı kapsam, şirket başına DB kimliği) bu sürümde yoktur.

## Alternatifler
- Yalnız uygulama filtresi: daha basit; tek hata = sızıntı. Reddedildi.
- `BYPASSRLS` worker: daha az politika; worker hatası tüm şirketleri açar. Reddedildi.
- SECURITY DEFINER giriş fonksiyonları: daha az kapsam anahtarı; ayrıcalıklı kod ve `search_path` riski. Reddedildi.

## Ne zaman değişir
Bağlantı başına sabit kimlik gerektiren uyum şartı veya RLS'in ölçülmüş performans maliyeti. Ölçüm yapılmadığı için performans iddiası yoktur.
