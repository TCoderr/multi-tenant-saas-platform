# 08 — Uygulama Planı

Aşamalar bağımlılık sırasıyla uygulanır. Bir aşamanın kabul kapısı geçmeden sonrakine başlanmaz; tamamlanan aşamaların testleri
sonraki aşamalarda da koşar. Her aşama sonunda `docs/implementation-progress.md` güncellenir (karar, dosya, komut/exit, test sayıları, kaynak parmak izi).

## Çözüm yapısı

```
multi-tenant-saas-platform/
  global.json, Directory.Build.props, Directory.Packages.props, Kulvar.slnx
  src/Kulvar.Platform | Kulvar.Modules.{Identity,Tenancy,Customers,Support,Billing,Operations} | Kulvar.Api | web/
  tests/Kulvar.Testing | Kulvar.UnitTests | Kulvar.IntegrationTests
  db/bootstrap-roles.sql            (rol oluşturma; container init ve test fixture ortak kullanır)
  deploy/postgres/init/01-roles.sh  compose.yaml  Dockerfile
  scripts/local-stack.ps1|.sh  source-fingerprint.ps1  stack-smoke.ps1  scan-secrets.ps1
  design/preview.html   docs/   artifacts/verification/   .github/workflows/ci.yml
```

## Aşamalar

| # | Kapsam | Kabul kapısı (özet) |
|---|---|---|
| 1 | Çözüm iskeleti, çekirdek (`DbScope`, çalıştırıcı, ProblemDetails), Identity (signup/login/logout/CSRF/oturum iptali/kilit), şirket oluşturma, üyelik okuma, rol/kaldırma/ayrılma, test altyapısı (Testcontainers + guard) | Bir kullanıcı iki şirket; iki kullanıcı izolasyonu; logout sonrası cookie reddi; son Owner değişmezi; self-escalation ve platform rolü signup'ı yok; gerçek PostgreSQL |
| 2 | RLS + roller + bileşik FK + trigger migration'ları; RLS/EF filtre testleri; Dockerfile, compose (HTTPS), init/seed betikleri, CI tanımı, migration doğrulamaları | Kapsamsız ham SQL fail closed; A kapsamında B için S/I/U/D reddi; bağlantı yeniden kullanımı A→B→kapsamsız; hata/iptal/yeniden denemede sızıntı yok; boş yığın, yeniden başlatma kalıcılığı, DB kapat/aç; seed idempotent |
| 3 | Müşteri ve talep: CRUD, arama, imleç, durum makinesi, atama, yorum, denetim, idempotency, ETag | Şirketler arası FK ve istek saldırıları; imleç kapsamı; eski düzenleme 412; replay tek etki; rollback'te kısmi veri yok; geçiş/rol kuralları |
| 4 | Davet (oluştur/iptal/kabul/süre), e-posta doğrulama, demo posta kutusu, sahiplik teklifi, outbox işleyicinin posta kısmı | Yanlış e-posta/şirket; paralel çift kabul; iptal/süresi dolmuş token; son Owner paralel kaldırma/devir; rol kaldırma eski oturum/idempotency ile aşılamaz; davet koltuk yarışı |
| 5 | Plan sürümleri, abonelik, koltuk ve aylık talep kotası, özellik kapıları, erişim modu | Son kota hakkına ≥ 20 eşzamanlı istek → tek başarı; iki şirket ayrı kota; replay/rollback sayaç doğru; dönem sınırı; downgrade yarışı ve limit üstü veri silinmez; askıda viewer/owner kurtarma politikası |
| 6 | Simüle sağlayıcı, plan değişikliği işlemi, webhook alımı/inbox, sıra/sürüm, zaman aşımı/mutabakat, iade/çözüm | Bilinmeyen eşleşme / geçersiz imza / çelişen gövde; yinelenen/sırasız; yanıt kaybı sonrası yeniden deneme; reddedilen plan entitlement açmaz; iptal/silme sonrası geç başarı yeniden etkinleştirmez; çözülemeyen sonuç terminal yola girer |
| 7 | Worker döngüleri, kiralama/fencing/DLQ/replay, async export, artefakt ömrü, şirket silme ve temizleme | Çökme/kiralama dolması/fencing'de çift etki yok; artefakt yetki/süre/path traversal/formül enjeksiyonu; A temizlenirken B ve ortak kullanıcı etkilenmez; silme × iş/export yarışı ve tekrar; şirketler arası posta kutusu sızıntısı yok |
| 8 | React arayüzünün tamamı (müşteri, talep, ekip, plan, denetim, export, ayarlar, posta kutusu, platform) | typecheck/lint/build/bileşen testleri; tarayıcıda A/B geçişi ve paralel sekme; geç gelen A yanıtı B ekranına yerleşmez; viewer/agent yasak eylemleri; güvenli davet/plan UX; erişilebilirlik/klavye |
| 9 | Sağlık, log/iz/metrik, hız sınırı, kapatma, güvenlik başlıkları, adillik, tarama betikleri, CI kapsamı | Log/izde kişisel veri yok; düşük kardinalite; operatör yalnız metadata; DB kesintisinde sahte başarı yok; worker adillik testleri; bağımlılık/sır taraması dürüst raporlanır |
| 10 | Final kapıları, belgeler, ekran görüntüleri, portföy özeti | Aynı kaynak parmak izi üzerinde kritik kapılar geçti; atlanan/başarısız izolasyon kapısı varken "tamamlandı" denmez |

Arayüz temeli (Vite iskeleti, tasarım belirteçleri, statik önizleme) Aşama 2 ile birlikte kurulur; ekranlar Aşama 8'de tamamlanır.

## Kapı komutları (özet)

| Kapı | Komut |
|---|---|
| Kilitli restore + Release build | `dotnet restore --locked-mode` · `dotnet build -c Release --no-restore` |
| Birim | `dotnet test --project tests/Kulvar.UnitTests -c Release --no-build` |
| Entegrasyon (gerçek PostgreSQL) | `dotnet test --project tests/Kulvar.IntegrationTests -c Release --no-build` |
| Web | `npm ci` · `npm run typecheck` · `npm run lint` · `npm test` · `npm run build` |
| Yığın duman testi | `scripts/stack-smoke.ps1` (kendi compose projesi/portu/dizini) |
| Tarayıcı E2E + erişilebilirlik | `npm run e2e` (çalışan izole yığına karşı) |
| Parmak izi | `scripts/source-fingerprint.ps1` |
| Tarama | `scripts/scan-secrets.ps1`, `dotnet list package --vulnerable --include-transitive`, `npm audit` |

## Riskler

| Risk | Önlem |
|---|---|
| RLS + EF etkileşimi (kapsam ayarı unutulursa her şey boş döner) | Tek çalıştırıcı; kapsamsız `AppDbContext` üretilemez; testler runtime rolüyle koşar |
| Kilit sırası kaynaklı deadlock | Belgeli kilit sırası + 40P01 yeniden deneme + eşzamanlılık testleri |
| Tek oturumda bitmeme | İlerleme ve sonraki adım dosyaları her aşamada güncel; test kayıtları kaynak parmak iziyle eşlenir |
| Playwright tarayıcı sürümü | Kurulu Chrome kanalı kullanılır; tarayıcı indirme zorunlu değil |
