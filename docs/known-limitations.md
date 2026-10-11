# Bilinen Sınırlar

Bu belge projenin **yapmadığı**, **simüle ettiği** ve **doğrulamadığı** şeyleri listeler. Buradaki maddeler hata değil, bilinçli kapsam sınırıdır;
üretime alınacak bir sistemde her birinin karşılığı gerekir.

## Simülasyon olan parçalar

| Parça | Gerçekte ne var |
|---|---|
| Ödeme sağlayıcı | Aynı veritabanında ayrı şemada (`fakepsp`) çalışan sahte sağlayıcı. Gerçek tahsilat, kart, 3D Secure, fatura, vergi, kısmi dönem (proration) yok. Para birimi yalnız TRY. "İade" yalnız durum değişikliğidir. |
| E-posta | Gönderim yok. İletiler veritabanındaki demo posta kutusuna yazılır ve yalnız alıcısı uygulama içinden okur. Teslim edilememe (bounce), SPF/DKIM, şablon yönetimi yok. |
| SMS / bildirim | Yok. |
| HTTPS | Yerel .NET geliştirici sertifikası. Üretim sertifikası, ters vekil (reverse proxy) ve sertifika yenileme kurulumu yok. |

## Kapsam dışı özellikler

- SSO (SAML/OIDC), çok faktörlü doğrulama (MFA), SCIM, özel alan adı (custom domain).
- **Parola sıfırlama ("şifremi unuttum"), e-posta değiştirme ve kullanıcı hesabı silme akışları yok.** Parolasını unutan demo kullanıcısı için yol yeni hesap açmaktır.
- Dosya eki, zengin metin, talep için e-posta ile yanıt, SLA zamanlayıcıları, etiketler, kayıtlı filtreler.
- Platform operatörü yalnız kurulum komutuyla (`--grant-operator`) atanır; operatör yönetimi için arayüz veya alt roller yok.
- Çoklu dil: arayüz ve hata iletileri yalnız Türkçe.
- Kubernetes, bulut dağıtımı, otomatik ölçekleme. Yalnız yerel Docker Compose doğrulandı.

## Mimari ve operasyon sınırları

- **Hız sınırı süreç içi bellektedir ve IP başınadır.** Birden çok API örneğinde her örnek kendi sayacını tutar. Ters vekil arkasında
  `X-Forwarded-For` işlenmediği için tüm istekler vekilin IP'sinden gelmiş sayılır. Şirket veya kullanıcı başına API hız sınırı yoktur
  (adalet yalnız arka plan iş kuyruğunda şirket başına parti sınırıyla uygulanır).
- **Tek API ve tek worker örneği** çalıştırılarak doğrulandı. Kiralama (lease) + fencing token tasarımı çoklu worker'a uygundur ve devralma/eski worker
  senaryoları testlidir; ancak birden çok kapsayıcıyla uzun süreli koşu yapılmadı.
- **Yük/performans testi yok.** Yanıt süresi, verim veya kapasite hakkında hiçbir iddia yoktur. RLS'in ve her istekteki oturum/üyelik
  okumasının yüksek trafikteki maliyeti ölçülmedi.
- Paylaşılan şema: bir şirketin ağır sorgusu aynı veritabanını paylaşan diğerlerini etkileyebilir (veritabanı düzeyinde kaynak izolasyonu yok).
- Dışa aktarma dosyaları yerel Docker volume'unda durur (API ve worker paylaşır); nesne depolama, bekleme hâlinde şifreleme ve virüs taraması yok.
  Büyük şirketler için parçalı/akışlı üretim ölçülmedi.
- Data Protection anahtarları volume'da şifresiz durur; anahtar kasası (KMS) entegrasyonu yok.
- **Yedekleme, geri yükleme, PITR yok.** "Kalıcı silme" yalnız canlı veritabanını ve export dosyalarını kapsar; yedek olmadığı için yedeklerden silme konusu ele alınmadı.
- Gözlemlenebilirlik: yapısal (JSON) konsol logu, `ActivitySource` ve `Meter` tanımlı; ancak dışa aktarıcı (OTLP), pano veya alarm kurulmadı.
- Denetim kaydı API rolü için yalnız-ekle (yetki düzeyinde) tutulur, ancak kurcalamaya karşı kanıtlanabilir (hash zinciri/WORM) değildir ve şirket kalıcı silinince onunla birlikte silinir.
- Zamanlayıcılar (deneme bitişi, ödemesiz dönem, export süresi, temizleme) worker'ın yoklama aralığıyla çalışır; olay tam anında değil, bir sonraki turda işlenir.
- Arama basit alt dizgi eşleşmesidir; tam metin indeksi ve Türkçe'ye özgü kök bulma yok.
- `FORCE ROW LEVEL SECURITY` nedeniyle migration rolü kiracı satırlarını göremez. Veri taşıyan bir migration, o iş için açık, dar ve geçici bir politika yazmayı gerektirir (bkz. ADR-0002).

## Doğrulama sınırları

- **Uzak CI hiç çalıştırılmadı.** `.github/workflows/ci.yml` tanımlıdır; push yapılmadığı için GitHub Actions üzerinde koşmamıştır. Yerelde geçen kapılar uzak CI'ın geçtiği anlamına gelmez.
- Tarayıcı testleri yalnız masaüstü Chrome (Chromium) ile koşuldu; Firefox, Safari ve gerçek mobil cihazlar denenmedi (mobil yalnız görüntü alanı boyutuyla).
- Erişilebilirlik otomatik araçla (axe) ve klavye akışlarıyla denetlendi; ekran okuyucuyla elle denetim yapılmadı.
- Zamanlama yan kanalları (yanıt süresinden hesap/şirket varlığı çıkarımı) ölçülmedi.
- Güvenlik denetimi yazarın kendi tehdit modeli ve negatif testleriyle sınırlıdır; bağımsız sızma testi yapılmadı.
- Bağımlılık zafiyet taraması (`dotnet list package --vulnerable`, `npm audit`) koşu anındaki veritabanlarını yansıtır; kapsayıcı imaj taraması yapılmadı.
- Sürüm yükseltme testleri yalnız bu deponun kendi migration zincirini ve sentetik veriyi kapsar; gerçek üretim verisiyle yükseltme deneyimi yoktur.

## Arayüz ve tarayıcı testlerinin kapsamadıkları

- Tarayıcı (E2E) testi **olmayan**, yalnız bileşen ve/veya API entegrasyon testiyle kapsanan akışlar: şirket silme isteği/iptali, abonelik iptali,
  sahiplik teklifi/kabulü, 412 çakışma çözümü, talep kotası dolu (403) ekranı.
- Platform panelinde "işlemi çöz" ve "ölü işi yeniden kuyruğa al" eylemleri tarayıcıda gerçek veriyle denenmedi (yalnız boş durumları görüldü); bu eylemler API entegrasyon testleriyle kapsanır.
- Yeni talep formundaki müşteri seçici ada göre ilk 100 müşteriyi listeler (arama destekli seçim ucu yok); daha büyük listede müşteri sayfasındaki "talep aç" kullanılır.
- Zaman çizelgesindeki atama olayı ve denetim kaydı yalnız kullanıcı kimliği taşır; ad, üye listesinden çözülür ve ayrılmış üye "Eski üye" olarak görünür.
- Kulvar şeridinin tonu şirket kimliğinden altı tondan biri olarak seçilir; iki şirket aynı tona düşebilir (monogram, ad ve sayfa başlığı yine farklıdır).
- Statik önizleme (`design/preview.html`) gerçek uygulama değildir; sabit örnek veriyle çizilmiş tasarım taslağıdır ve "Demo önizleme" etiketi taşır.
