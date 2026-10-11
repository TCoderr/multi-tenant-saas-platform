<#
.SYNOPSIS
  Arka uç kapıları: kilitli restore, Release build (uyarılar hata), birim testleri, gerçek PostgreSQL entegrasyon testleri, kaynak parmak izi.
.DESCRIPTION
  Her adımın exit kodu ve test sayıları (toplam/geçen/başarısız/atlanan) özetlenir; ham çıktılar artifacts/verification/ altına yazılır.
  Entegrasyon testleri kendi Testcontainers PostgreSQL'ini başlatır; geliştirme yığınına dokunmaz.
  -Web: web kapılarını da (typecheck, lint, bileşen testleri, üretim build) koşar.
#>
param([switch]$Web)
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
$logDir = Join-Path $root 'artifacts\verification'
New-Item -ItemType Directory -Force $logDir | Out-Null
$rows = New-Object System.Collections.Generic.List[object]

function Step([string]$Name, [string]$LogName, [scriptblock]$Command) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & $Command 2>&1 | ForEach-Object { "$_" }
    $exit = $LASTEXITCODE
    $log = Join-Path $logDir $LogName
    # Testcontainers kurulum satırları test parolaları içerir; loga yazılmaz.
    $output | Where-Object { $_ -notmatch 'testcontainers\.org|_password=' } | Set-Content -Path $log -Encoding utf8
    $text = $output -join "`n"
    $counts = ''
    if ($text -match 'toplam:\s*(\d+)[\s\S]*?başarısız oldu:\s*(\d+)[\s\S]*?başarılı:\s*(\d+)[\s\S]*?atlandı:\s*(\d+)') {
        $counts = "toplam=$($Matches[1]) geçti=$($Matches[3]) başarısız=$($Matches[2]) atlandı=$($Matches[4])"
    } elseif ($text -match 'total:\s*(\d+)[\s\S]*?failed:\s*(\d+)[\s\S]*?succeeded:\s*(\d+)[\s\S]*?skipped:\s*(\d+)') {
        $counts = "toplam=$($Matches[1]) geçti=$($Matches[3]) başarısız=$($Matches[2]) atlandı=$($Matches[4])"
    } elseif ($text -match 'Tests\s+(\d+) passed') {
        $counts = "geçti=$($Matches[1])"
    }
    $rows.Add([pscustomobject]@{ Step = $Name; Exit = $exit; Counts = $counts; Seconds = [int]$watch.Elapsed.TotalSeconds; Log = "artifacts/verification/$LogName" })
    Write-Host ("{0,-46} exit={1} {2} ({3} sn)" -f $Name, $exit, $counts, [int]$watch.Elapsed.TotalSeconds)
}

Step 'dotnet restore --locked-mode' 'restore.log' { dotnet restore Kulvar.slnx --locked-mode }
Step 'dotnet build -c Release' 'build.log' { dotnet build Kulvar.slnx -c Release --no-restore }
Step 'birim testleri' 'unit-tests.log' { dotnet test --project tests/Kulvar.UnitTests -c Release --no-build }
Step 'entegrasyon testleri (PostgreSQL 18)' 'integration-tests.log' { dotnet test --project tests/Kulvar.IntegrationTests -c Release --no-build }
if ($Web) {
    Push-Location (Join-Path $root 'src\web')
    Step 'web: npm ci' 'web-install.log' { npm ci --no-audit --no-fund }
    Step 'web: typecheck' 'web-typecheck.log' { npm run typecheck }
    Step 'web: lint' 'web-lint.log' { npm run lint }
    Step 'web: bileşen testleri' 'web-tests.log' { npm test }
    Step 'web: üretim build' 'web-build.log' { npm run build }
    Pop-Location
}
$fingerprint = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'source-fingerprint.ps1')
Write-Host "parmak izi: $fingerprint"
$summary = @("# doğrulama özeti $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) parmak izi: $fingerprint") +
    ($rows | ForEach-Object { "{0}`texit={1}`t{2}`t{3} sn`t{4}" -f $_.Step, $_.Exit, $_.Counts, $_.Seconds, $_.Log })
[System.IO.File]::WriteAllLines((Join-Path $logDir 'verify-summary.log'), $summary, (New-Object System.Text.UTF8Encoding($false)))
exit $(if (@($rows | Where-Object { $_.Exit -ne 0 }).Count -eq 0) { 0 } else { 1 })
