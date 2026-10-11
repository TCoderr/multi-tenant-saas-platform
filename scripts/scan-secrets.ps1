<#
.SYNOPSIS
  Depo taraması: sır/kimlik bilgisi desenleri, repoya girmemesi gereken dosyalar, gerçek görünümlü e-posta alan adları,
  yazarlık/imza kuralı ve bağımlılık zafiyet denetimi (NuGet + npm).
.DESCRIPTION
  Yalnız git'in göreceği dosyalar taranır (izlenen + izlenmeyen, .gitignore dışında kalanlar); yani "commit edilse ne girerdi" sorusu yanıtlanır.
  Bulgular dosya:satır olarak yazılır; eşleşen DEĞER yazılmaz (maskelenir). Çıktı: artifacts/verification/scan-*.log.
  -SkipAudit: ağ gerektiren zafiyet denetimlerini atlar (atlandığı özet satırında açıkça yazılır).
  Exit 0: bulgu yok. Exit 1: en az bir bulgu veya başarısız denetim.
#>
param([switch]$SkipAudit)
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
$logDir = Join-Path $root 'artifacts\verification'
New-Item -ItemType Directory -Force $logDir | Out-Null
$findings = New-Object System.Collections.Generic.List[string]
$summary = New-Object System.Collections.Generic.List[string]
$self = 'scripts/scan-secrets.ps1'

$files = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
$binary = '\.(png|jpg|jpeg|gif|ico|woff2?|ttf|otf|zip|pdf|pfx)$'
$text = $files | Where-Object { $_ -notmatch $binary -and $_ -ne $self }

# 1) Repoya girmemesi gereken dosyalar.
$forbiddenPaths = $files | Where-Object { $_ -match '(^|/)\.local(-[^/]+)?/' -or $_ -match '\.(pfx|pem|key|p12)$' -or ($_ -match '(^|/)\.env(\..+)?$' -and $_ -notmatch '\.env\.example$') -or $_ -match '(^|/)Seed__Passwords__' }
foreach ($path in $forbiddenPaths) { $findings.Add("yasak-dosya`t$path") }
$summary.Add("yasak dosya (sır/sertifika/.env/.local): $(@($forbiddenPaths).Count)")

# 2) Sır desenleri. Değer loglanmaz.
$secretPatterns = [ordered]@{
    'ozel-anahtar'      = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    # Bağlantı dizesi biçimi (boşluksuz "Password=değer"); değişken/yer tutucu ($x, {x}, <x>, ***) sayılmaz.
    'baglanti-parolasi' = '(?i)\bPassword=(?![\$\{<\*;\|"'']|\.\.\.)[^;\s"''`\)\}]{6,}'
    'atanmis-sir'       = '(?i)\b(api[_-]?key|client[_-]?secret|access[_-]?token|webhook[_-]?secret)\b\s*[:=]\s*["''][A-Za-z0-9+/_\-]{20,}["'']'
    'aws-anahtari'      = '\bAKIA[0-9A-Z]{16}\b'
    'github-token'      = '\bgh[pousr]_[A-Za-z0-9]{36,}\b'
    'slack-token'       = '\bxox[baprs]-[A-Za-z0-9-]{10,}'
    'jwt'               = '\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.'
}
# Bilinçli istisnalar (gerekçeli): yalnız test kodundaki, çalışma anında üretilen veya açıkça sahte sabitler.
$secretAllow = @(
    'tests/Kulvar.IntegrationTests/Infrastructure/PostgresFixture.cs', # Tek kullanımlık Testcontainers veritabanının rol parolaları; koşu bitince kapsayıcı silinir.
    'tests/Kulvar.IntegrationTests/Infrastructure/KulvarApp.cs'        # Yalnız test sürecinde kullanılan webhook sırrı sabiti.
)
# Ürün kodu, betikler ve dağıtım dosyalarında sabit parola dizgisi: boşluksuz, en az 8 karakterlik atanmış değer; cümleler (doğrulama iletileri) sayılmaz (test girdileri hariç: onlar tek kullanımlık test veritabanına gider).
$literalPassword = '(?i)\b(password|parola|passwd|pwd)\b\s*[:=]\s*["''][^"''\s\$\{<]{8,}["'']'
$secretCount = 0
foreach ($file in ($text | Where-Object { $_ -notmatch '^tests/' -and $_ -notmatch '\.(test|spec)\.tsx?$' -and $_ -notmatch '^src/web/(e2e|src/test)/' })) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $root $file))
    for ($i = 0; $i -lt $lines.Length; $i++) {
        # psql değişkeni (PASSWORD :'degisken') bir yer tutucudur, değer değil.
        if ($lines[$i] -match $literalPassword -and $lines[$i] -notmatch "PASSWORD :'[a-z_]+'") { $findings.Add("sir:sabit-parola`t${file}:$($i + 1)"); $secretCount++ }
    }
}
foreach ($file in $text) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $root $file))
    for ($i = 0; $i -lt $lines.Length; $i++) {
        foreach ($name in $secretPatterns.Keys) {
            if ($lines[$i] -match $secretPatterns[$name]) {
                if ($secretAllow -contains $file) { continue }
                $findings.Add("sir:$name`t${file}:$($i + 1)")
                $secretCount++
            }
        }
    }
}
$summary.Add("sır deseni bulgusu: $secretCount (istisna dosyası: $($secretAllow.Count), yalnız test altyapısı)")

# 3) Sentetik veri: e-posta adresleri yalnız ayrılmış/sahte alan adlarında olmalı (RFC 2606/6761 ve ürünün kendi .example adları).
$emailPattern = '[A-Za-z0-9._%+\-]+@([A-Za-z0-9\-]+\.)+[A-Za-z]{2,}'
$emailAllowed = '(?i)@(([a-z0-9\-]+\.)*(example|test|invalid|localhost|local)|example\.(com|org|net)|users\.noreply\.github\.com)$'
$emailIgnoreFiles = '(^|/)(package-lock\.json|packages\.lock\.json|THIRD-PARTY-NOTICES\.md)$' # üçüncü taraf paket meta verisi / zorunlu atıf
$emailCount = 0
foreach ($file in ($text | Where-Object { $_ -notmatch $emailIgnoreFiles })) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $root $file))
    for ($i = 0; $i -lt $lines.Length; $i++) {
        foreach ($match in [regex]::Matches($lines[$i], $emailPattern)) {
            $value = $match.Value
            if ($value -match $emailAllowed) { continue }
            if ($value -match '@\d' -or $value -match '\.(png|svg|css|js|ts|tsx|json)$' -or $value -match '^(git|npm)@') { continue } # sürüm/dosya adı, e-posta değil
            $findings.Add("gercek-gorunumlu-eposta`t${file}:$($i + 1)`t@$(($value -split '@')[1])")
            $emailCount++
        }
    }
}
$summary.Add("ayrılmış olmayan alan adında e-posta: $emailCount")

# 4) Yazarlık kuralı: sahibi TCoderr; otomatik araç/yardımcı imzası veya ortak yazar satırı yok.
$signaturePattern = '(?i)(co-authored-by|generated (with|by)|noreply@anthropic|\bauthored by (an? )?(ai|llm)\b)'
$toolNames = '(?i)\b(claude|anthropic|codex|openai|chatgpt|copilot)\b'
# Araç adının geçmesine izin verilen yerler: yalnız olgu bildirimi (yönerge dosyası bulunmadı; okunan beceri kaynağının yolu ve lisansı).
$toolNameAllow = @('docs/implementation-progress.md', 'docs/skills-and-sources.md', 'docs/final-verification.md')
$signatureCount = 0
$mentionCount = 0
foreach ($file in ($text | Where-Object { $_ -notmatch '(^|/)(package-lock\.json|packages\.lock\.json)$' })) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $root $file))
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match $signaturePattern) { $findings.Add("imza`t${file}:$($i + 1)"); $signatureCount++ }
        elseif ($lines[$i] -match $toolNames -and $toolNameAllow -notcontains $file) { $findings.Add("arac-adi`t${file}:$($i + 1)"); $mentionCount++ }
    }
}
$summary.Add("otomatik araç imzası / ortak yazar satırı: $signatureCount; izinli olgu belgeleri dışında araç adı: $mentionCount")
$ownerOk = (Select-String -Path 'Directory.Build.props' -Pattern '<Authors>TCoderr</Authors>' -Quiet) -and (Select-String -Path 'src/web/package.json' -Pattern '"author":\s*"TCoderr"' -Quiet)
if (-not $ownerOk) { $findings.Add("sahiplik`tDirectory.Build.props / src/web/package.json içinde TCoderr bulunamadı") }
$summary.Add("paket sahipliği TCoderr (NuGet + npm meta verisi): $ownerOk")

# 5) Git durumu: commit/remote oluşturulmadı; depo-yerel kimlik ayarı yazılmadı (global kimliğe dokunulmaz, okunmaz).
$commitCount = (git rev-list --all --count 2>$null); if (-not $commitCount) { $commitCount = 0 }
$remotes = @(git remote)
$localIdentity = @(git config --local --get-regexp '^user\.' 2>$null)
$summary.Add("git: commit=$commitCount remote=$($remotes.Count) depo-yerel kimlik ayarı=$($localIdentity.Count)")
if ($remotes.Count -gt 0) { $findings.Add("git`tremote tanımlı: $($remotes -join ',')") }
if ($localIdentity.Count -gt 0) { $findings.Add("git`tdepo-yerel user.* ayarı var") }

# 6) Bağımlılık zafiyet denetimi (ağ gerekir).
$auditExit = 0
if ($SkipAudit) {
    $summary.Add('zafiyet denetimi: ATLANDI (-SkipAudit)')
} else {
    $nuget = dotnet list Kulvar.slnx package --vulnerable --include-transitive 2>&1 | ForEach-Object { "$_" }
    $nuget | Set-Content -Path (Join-Path $logDir 'scan-nuget-vulnerable.log') -Encoding utf8
    $nugetExit = $LASTEXITCODE
    $nugetHits = @($nuget | Where-Object { $_ -match '^\s*>\s' }).Count
    $summary.Add("NuGet zafiyet denetimi (doğrudan + geçişli): exit=$nugetExit zafiyetli paket satırı=$nugetHits")
    if ($nugetExit -ne 0 -or $nugetHits -gt 0) { $findings.Add("zafiyet`tNuGet: artifacts/verification/scan-nuget-vulnerable.log"); $auditExit = 1 }

    Push-Location (Join-Path $root 'src\web')
    $npm = npm audit --audit-level=low 2>&1 | ForEach-Object { "$_" }
    $npmExit = $LASTEXITCODE
    Pop-Location
    $npm | Set-Content -Path (Join-Path $logDir 'scan-npm-audit.log') -Encoding utf8
    $summary.Add("npm audit (üretim + geliştirme bağımlılıkları, eşik: low): exit=$npmExit")
    if ($npmExit -ne 0) { $findings.Add("zafiyet`tnpm: artifacts/verification/scan-npm-audit.log"); $auditExit = 1 }
}

$fingerprint = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'source-fingerprint.ps1')
$header = "# depo taraması $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) parmak izi: $fingerprint taranan dosya: $(@($files).Count)"
$report = @($header) + $summary + @("bulgu: $($findings.Count)") + $findings
[System.IO.File]::WriteAllLines((Join-Path $logDir 'scan-summary.log'), $report, (New-Object System.Text.UTF8Encoding($false)))
$report | ForEach-Object { Write-Host $_ }
exit $(if ($findings.Count -eq 0) { 0 } else { 1 })
