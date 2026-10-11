# 07 — Arayüz Tasarımı

Kullanılan rehber: yerel resmi eklenti önbelleğindeki `frontend-design` skill'i (Apache-2.0; kaynak ve kullanım kaydı
[skills-and-sources.md](skills-and-sources.md)). Erişilebilirlik için ayrı bir skill bulunmadı; WCAG 2.2 AA hedefleri bu belgede elle tanımlandı
ve axe ile otomatik, klavye ile elle denetlenir.

## 1. Konu ve kitle

Destek ekibinin gün boyu açık tuttuğu çalışma alanı: müşteri kaydı arar, talep açar, yorum yazar, durum değiştirir.
Bazı kullanıcılar aynı gün iki farklı şirkette çalışır. Arayüzün birinci işi: **şu an hangi şirkettesin** sorusunu hiç sordurmamak.

## 2. Tasarım planı

### Renk
| Ad | Değer | Kullanım |
|---|---|---|
| Kâğıt | `#F3F6F4` | Sayfa zemini (soğuk, hafif yeşil-gri; krem değil) |
| Yüzey | `#FFFFFF` | Tablo, form, panel |
| Mürekkep | `#14261F` | Ana metin (koyu çam) |
| Soluk mürekkep | `#51615A` | İkincil metin |
| Çizgi | `#D3DBD6` | Ayırıcılar, kenarlıklar |
| Eylem | `#0F6E5C` | Birincil düğme, bağlantı, odak halkası |

Durum renkleri her zaman metin + simge ile birlikte kullanılır (renk tek anlam kaynağı değildir):
tamam `#1B6B3A` / `#E4F2E8`, uyarı `#7A5200` / `#FBF1D9`, kritik `#9F2A1F` / `#FBE6E2`, bilgi `#1F4E8C` / `#E6EEF8`.
Tüm metin/zemin çiftleri ≥ 4,5:1 kontrast.

### Tipografi
Tek süper aile: **IBM Plex Sans** (gövde ve başlık, 400/500/600) ve **IBM Plex Sans Condensed** (kulvar şeridi ve tablo sütun başlıkları, 500/600).
Türkçe glifleri (ğ, ş, ı, İ) tamdır; npm paketinden (`@fontsource/*`, OFL-1.1) aynı origin'den sunulur, CDN yoktur.
Ölçek: 13 / 15 / 17 / 21 / 27 px; gövde 15 px, satır yüksekliği 1,5; sayılar `tabular-nums`. Cümle düzeni; büyük harfli etiket yok.

### Yerleşim
İmza öğe **kulvar şeridi**: çalışma alanının sol kenarında, şirkete özgü renk + desen + iki harfli monogram + dikey şirket adı taşıyan
48 px'lik sabit şerit. Şirket değişince şerit değişir; iki sekme yan yana durduğunda hangi sekmenin hangi şirket olduğu bakışla ayırt edilir.
Şerit rengi şirket kimliğinden türetilen altı tondan biridir ve her ton farklı bir çizgi deseniyle eşlenir (renk körlüğünde de ayırt edilir).

```
 1440 px
 ┌────┬──────────────┬────────────────────────────────────────────────────┐
 │ AM │ Akasya       │ Talepler                              [Yeni talep] │
 │ ▒▒ │ Mobilya      │ Durum ▾  Öncelik ▾  Atanan ▾   Ara…                │
 │ A  │ Rol: Admin   │ ────────────────────────────────────────────────── │
 │ k  │ ──────────── │  #   Başlık           Müşteri    Öncelik  Atanan   │
 │ a  │ Müşteriler   │ 104  Kargo gecikmesi  D. Arslan  Yüksek   Aslı K.  │
 │ s  │ Talepler     │ 103  Fatura düzeltme  …                            │
 │ y  │ Ekip         │                                                    │
 │ a  │ Plan         │                                                    │
 │    │ Denetim      │                                                    │
 │    │ Dışa aktarma │                                                    │
 └────┴──────────────┴────────────────────────────────────────────────────┘
   şerit  gezinme (220)   içerik (sola hizalı, en çok 1180 px)

 768 px: şerit kalır; gezinme içeriğin üstünde yatay sekme satırı olur.
 360 px: şerit üstte yatay banda dönüşür (monogram + şirket adı + menü düğmesi); tablolar satır kartlarına (tanım listesi) açılır.
```

### İlkeler
1. **Bağlam asla belirsiz değil.** Şerit, sayfa başlığı ve `document.title` şirket adını taşır. Şirketsiz ekranlarda (giriş, şirket seçimi, posta kutusu, platform) şerit yoktur.
2. **Yoğun ama sakin.** Tablolar ve düz bölümler; her şeyi karta bölmek yok. Kenarlık yarıçapı yalnız kontrollerde (4 px); gölge yalnız açılır katmanlarda.
3. **Durumlar dürüst.** Her durumun kendi metni ve bir sonraki adımı vardır; sahte başarı veya bağlanmamış düğme yoktur.
4. **Rol şeffaf.** Yapılamayan eylem gizlenmez, devre dışı bırakılır ve nedenini söyler ("Bu işlem için Owner rolü gerekir"). Yetkiyi sunucu belirler.

### Plana karşı gözden geçirme
İlk taslakta krem zemin + serif başlık ve kart ızgarası vardı; ikisi de konudan bağımsız varsayılanlardı (ayrıca komşu etkinlik projesi krem/kırmızı,
lojistik projesi gri/mavi kullanıyor). Zemin soğuk yeşil-griye, tip tek sans aileye, liste ekranları tabloya çevrildi.
Mor gradyan, parıltı, 3B, sayfa yüklenince kayan bölümler yok. Hareket yalnız kullanıcı eylemine yanıt verir (açılır menü, bildirim) ve
`prefers-reduced-motion` altında kapanır.

## 3. Ekranlar

| Ekran | Yol | İçerik |
|---|---|---|
| Ürün/fiyat | `/` | Kısa ürün açıklaması, plan tablosu (API'den), giriş/kayıt bağlantıları |
| Kayıt / giriş | `/signup`, `/login` | Form, genel hata, hız sınırı mesajı |
| E-posta doğrulama | `/verify-email#token=` | Token'ı gönderir, sonucu gösterir |
| Davet kabul | `/invite#token=` | Şirket adı, rol, e-posta eşleşmesi; kabul; süresi dolmuş/iptal durumları |
| Şirketlerim | `/app` | Üyelik listesi (ad, rol, durum), şirket oluşturma |
| Demo posta kutusu | `/mail` | Kendi adresine gelen iletiler; "Demo" etiketi |
| Müşteriler | `/t/:tenantId/customers`, `/customers/:id` | Liste, arama, form, detay + talepleri |
| Talepler | `/t/:tenantId/tickets`, `/tickets/:id` | Liste + filtre, detay, zaman çizelgesi, yorum, atama, geçiş |
| Ekip | `/t/:tenantId/team` | Üyeler, roller, davetler, sahiplik teklifi |
| Plan ve kullanım | `/t/:tenantId/plan` | Plan, limit çubukları, plan değişikliği, işlem durumu |
| Denetim | `/t/:tenantId/audit` | Olay listesi |
| Dışa aktarma | `/t/:tenantId/exports` | İş listesi, indirme |
| Şirket ayarları | `/t/:tenantId/settings` | Ad, silme isteği/iptali, şirketten ayrılma |
| Platform | `/platform` | Şirket metadata'sı, askıya alma, çözüm kuyruğu, ölü işler |

## 4. Durumlar

| Durum | Sunum |
|---|---|
| Yükleniyor | İskelet satırlar; `aria-busy`. 300 ms altında gösterilmez |
| Boş | Ne olmadığını ve ilk adımı söyler ("Henüz müşteri yok. İlk müşteriyi ekleyin.") |
| Hata | Ne olduğu + yeniden dene düğmesi; `traceId` küçük metin |
| Çevrimdışı | Üst bant: "Bağlantı yok. Değişiklikler gönderilmedi." Formlar veriyi korur |
| Çakışma (412) | "Bu kayıt siz düzenlerken değişti." Güncel sürümü yükle / kendi metnimi koru seçenekleri |
| Kota doldu | Talep formunda: limit, kullanılan, sıfırlanma tarihi; Owner'a plan bağlantısı |
| Koltuk doldu | Davet formunda aynı kalıp |
| PastDue | Sarı bant: ödemesiz dönemin bitiş tarihi; Owner'a "Planı güncelle" |
| Suspended / Cancelled | Kırmızı bant: içerik salt-okunur; Owner'a kurtarma bağlantısı; yazma düğmeleri nedenli devre dışı |
| Platform askısı | Tam sayfa durum: şirket askıda, içerik kapalı |
| Silme bekliyor | Kırmızı bant: kalıcı silme tarihi; Owner'a "Silmeyi iptal et" |
| Davet süresi dolmuş/iptal | Açıklama + "Yöneticinizden yeni davet isteyin" |
| Yetkisiz (403) | Gereken rol belirtilir |
| Oturum bitti (401) | Önbellek temizlenir, girişe götürür, geldiği yol korunur |

## 5. Erişilebilirlik

- Her form alanında görünür `<label>`; hata metni `aria-describedby` ile alana bağlı, `aria-invalid` işaretli; hata özeti `role="alert"`.
- Odak halkası her etkileşimli öğede görünür (2 px eylem rengi + 2 px boşluk). Sayfa değişiminde odak ana başlığa taşınır; "içeriğe atla" bağlantısı vardır.
- Tablolar gerçek `<table>` (başlık hücreleri `scope`); dar ekranda satır kartları `<dl>`.
- Açılır menü ve iletişim kutuları klavye ile açılır/kapanır (Esc), odak tuzağı ve geri dönüşü vardır.
- Durum rozetleri metin içerir; canlı güncellemeler `aria-live="polite"`.
- Otomatik: axe (WCAG 2.1 A/AA kuralları) kritik ekranlarda 360/768/1440 genişlikte. Elle: klavye turu. Ekran okuyucu ile elle test yapılmadıysa bu açıkça raporlanır.

## 6. Veri ve önbellek kuralları (istemci)

- Şirket kimliği yalnız URL'den okunur; global "aktif şirket" durumu yoktur.
- Sorgu anahtarları `['u', userId, 't', tenantId, …]`. Logout, kullanıcı değişimi ve `401`'de önbellek tamamen temizlenir.
- Kota/koltuk sayaçları sunucudan gelir; iyimser sayaç yoktur. Değişiklik sonrası ilgili sorgular geçersiz kılınır.
- Plan işlemi ve export durumu 2 saniyelik polling ile izlenir; gerçek zamanlı bağlantı yoktur.
- `localStorage`/`sessionStorage` kullanılmaz.

## 7. Statik önizleme

`design/preview.html`: API'siz, tek dosyalık görsel önizleme; her ekranda "Demo önizleme" etiketi. Nihai React arayüzü gerçek API'ye bağlıdır.
