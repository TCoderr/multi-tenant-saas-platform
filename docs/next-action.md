# Sonraki Adım

Durum: **Aşama 0–10 tamam.** Tüm kapılar tek kaynak parmak izinde (`b30967daa618d93e`) geçti; ayrıntı `docs/final-verification.md`.
Yapılacak zorunlu iş kalmadı. Commit, push, PR, remote ve dağıtım yapılmadı (bilerek).

## İsteğe bağlı sonraki adımlar (sahibinin kararı)

1. İlk commit ve uzak depo: yapılırsa `.github/workflows/ci.yml` ilk kez uzakta koşacaktır (şimdiye dek koşmadı). Yazarlık kuralı: sahip TCoderr; otomatik araç imzası/ortak yazar satırı eklenmez.
2. `docs/known-limitations.md` içindeki boşluklardan öncelikliler: parola sıfırlama akışı; platform "işlemi çöz / yeniden kuyruğa al" eylemleri için tarayıcı testi;
   müşteri seçici için arama ucu; ters vekil arkasında hız sınırı (`X-Forwarded-For`); OTLP dışa aktarıcı.
3. Yük testi yapılmadan performans iddiasında bulunulmamalıdır.

## Yeniden doğrulama

`scripts/verify.ps1 -Web` → `scripts/stack-smoke.ps1` → `scripts/e2e.ps1` → `scripts/scan-secrets.ps1`.
Kaynak değişirse parmak izi değişir ve kayıtlı sonuçlar geçerliliğini yitirir; dört betik aynı parmak izinde yeniden koşulmalıdır.

## Ortam notları

- PowerShell betikleri UTF-8 BOM ile kaydedilmelidir (Windows PowerShell 5.1 BOM'suz dosyayı ANSI okur).
- Betikleri çağırırken `2>&1` kullanılmaz (docker'ın stderr ilerleme satırları sahte hata olur).
- Migration üretimi: `dotnet ef migrations add <Ad> --project src/Kulvar.Api --startup-project src/Kulvar.Api --output-dir Migrations`;
  güvenlik SQL'i `Migrations/Sql/M000N.cs` içine yazılır ve migration'ın `Up` sonuna / `Down` başına ve sonuna bağlanır.
- Yeni tablo eklenince: `tests/.../Stage2/SchemaCatalog.cs` ve `MigrationTests.UpgradeSeeds` güncellenir; aksi halde testler kırılır.
- Testler tek veritabanını ve tek sahte saati paylaşır; kuyruk/sayım iddia eden testler önce ilgili kuyruğu boşaltmalı veya hedefi doğrudan işlemelidir.
- API yanıtlarında ASCII dışı harfler `\uXXXX` olarak kaçırılır (geçerli JSON); ham metinde Türkçe karakter aramak yerine JSON ayrıştırılmalıdır.
- Yerel sırlar ve demo parolaları `.local/` altındadır (git'e girmez); `local-stack init` var olan dosyaları korur.
