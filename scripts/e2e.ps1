<#
.SYNOPSIS
  Tarayıcı uçtan uca testleri (Playwright + axe): kendi compose projesi (kulvar-e2e), kendi portu (8863) ve kendi yerel dizini
  (.local-e2e) ile çalışır. Geliştirme yığınına (kulvar), duman testi yığınına (kulvar-smoke) ve komşu projelere dokunmaz.
.DESCRIPTION
  Akış: (varsa) önceki koşudan kalan kendi yığınını temizle -> local-stack init -SeedDemo -> up -SeedDemo -Simulation ->
  src/web içinde "npm run e2e" -> her durumda kendi yığınını (volume'lar dahil) ve .local-e2e dizinini sil -> Playwright çıkış kodu.
  Tek IP'den çok sayıda giriş yapıldığı için giriş/kayıt hız sınırları yalnız bu izole yığında yükseltilir.
  Demo parolaları koşuya özeldir, .local-e2e\seed altında durur ve ekrana/loga yazılmaz.
  Bu betik "2>&1" ile çağrılmamalıdır (docker'ın stderr ilerleme satırları sahte hataya dönüşür).
.PARAMETER KeepRunning
  Koşu bitince yığını ve .local-e2e dizinini bırakır (inceleme için). Sonraki koşu başlarken ikisi de temizlenir.
.PARAMETER Grep
  Yalnız adı eşleşen testleri koşturur (Playwright --grep).
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e2e.ps1
#>
param(
    [switch]$KeepRunning,
    [string]$Grep = ''
)
$ErrorActionPreference = 'Stop'
$Project = 'kulvar-e2e'
$LocalDir = '.local-e2e'
$Port = 8863

$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
$stack = Join-Path $PSScriptRoot 'local-stack.ps1'
$local = Join-Path $root $LocalDir
$web = Join-Path $root 'src\web'
$report = Join-Path $web 'test-results\e2e-results.json'
$started = Get-Date
$exit = 1
$previousLogin = $env:KULVAR_LOGIN_LIMIT
$previousSignup = $env:KULVAR_SIGNUP_LIMIT

function Remove-OwnStack {
    # Yalnız bu koşunun kaynakları silinir: reset, her volume'un Kulvar etiketini doğrular ve yalnız "kulvar-e2e" projesini indirir.
    try {
        & $stack reset -Project $Project -LocalDir $LocalDir -Port $Port -ConfirmReset $Project
    }
    catch {
        Write-Host "Uyarı: yığın temizliği tamamlanamadı: $($_.Exception.Message)"
    }
    if ((Test-Path $local) -and ((Split-Path $local -Leaf) -eq '.local-e2e') -and ((Split-Path $local -Parent) -eq $root)) {
        Remove-Item -LiteralPath $local -Recurse -Force
    }
}

function Test-PortFree([int]$Number) {
    $listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, $Number)
    try {
        $listener.Start()
        $listener.Stop()
        return $true
    }
    catch {
        return $false
    }
}

try {
    if (-not (Test-Path (Join-Path $web 'node_modules\@playwright\test\cli.js'))) {
        throw 'src\web bağımlılıkları kurulu değil. Önce src\web içinde "npm ci" çalıştırın.'
    }

    # Önceki koşudan kalan (ör. -KeepRunning veya yarıda kesilen) kendi yığınımız: temiz başlangıç.
    Remove-OwnStack
    if (-not (Test-PortFree $Port)) { throw "Port $Port kullanımda. E2E yığını başlatılmadı; başka hiçbir şeye dokunulmadı." }
    if (Test-Path $report) { Remove-Item -LiteralPath $report -Force }

    $env:KULVAR_LOGIN_LIMIT = '600'
    $env:KULVAR_SIGNUP_LIMIT = '120'
    & $stack init -Project $Project -LocalDir $LocalDir -Port $Port -SeedDemo
    & $stack up -Project $Project -LocalDir $LocalDir -Port $Port -SeedDemo -Simulation

    $env:KULVAR_E2E_BASE_URL = "https://localhost:$Port"
    $env:KULVAR_E2E_LOCAL_DIR = $local
    Push-Location $web
    try {
        if ($Grep) { & npm.cmd run e2e -- --grep $Grep } else { & npm.cmd run e2e }
        $exit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
catch {
    Write-Host "E2E koşusu tamamlanamadı: $($_.Exception.Message)"
    $exit = 1
}
finally {
    if ($KeepRunning) {
        Write-Host "Yığın bırakıldı: https://localhost:$Port (proje $Project, dizin $LocalDir). Temizlik: .\scripts\local-stack.ps1 reset -Project $Project -LocalDir $LocalDir -Port $Port -ConfirmReset $Project"
    }
    else {
        Remove-OwnStack
    }
    $env:KULVAR_LOGIN_LIMIT = $previousLogin
    $env:KULVAR_SIGNUP_LIMIT = $previousSignup
    Remove-Item Env:KULVAR_E2E_BASE_URL -ErrorAction SilentlyContinue
    Remove-Item Env:KULVAR_E2E_LOCAL_DIR -ErrorAction SilentlyContinue
}

$elapsed = [int]((Get-Date) - $started).TotalSeconds
if (Test-Path $report) {
    $stats = (Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json).stats
    Write-Host ("E2E sonucu: geçen={0} başarısız={1} kararsız={2} atlanan={3} süre={4} sn" -f $stats.expected, $stats.unexpected, $stats.flaky, $stats.skipped, $elapsed)
    if ($stats.skipped -gt 0 -and $exit -eq 0) {
        Write-Host 'Atlanan test var: koşu başarılı sayılmaz.'
        $exit = 1
    }
}
else {
    Write-Host "E2E sonucu: test raporu oluşmadı (yığın kurulamadı veya testler başlamadı). Süre=$elapsed sn"
}
exit $exit
