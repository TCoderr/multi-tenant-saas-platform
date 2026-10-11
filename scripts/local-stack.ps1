<#
.SYNOPSIS
  Kulvar yerel Docker yığını: secret/sertifika hazırlığı, başlatma, durdurma, durum, log, demo seed, (açık onaylı) sıfırlama.
.DESCRIPTION
  init   : LocalDir altında rastgele parolalar, bağlantı dizeleri, webhook sırrı ve HTTPS sertifikası (mevcut .NET geliştirici
           sertifikasının PFX dışa aktarımı; sertifika deposu değişmez). -SeedDemo ile demo kullanıcı parolaları da üretilir.
           Değerler ekrana yazılmaz; yalnız dosyalarda (git'e girmez) durur. Yinelenebilir: var olan dosyalar korunur.
  up     : image build + compose up (sağlıklı olana kadar bekler). -SeedDemo: ardından demo verisini kurar (yinelenebilir).
  seed   : yalnız demo verisini kurar (yığın çalışıyor olmalı).
  down   : yığını durdurur. Volume'lar (veritabanı, anahtarlar, export'lar) KALIR.
  status : compose ps.
  logs   : compose logs (son 200 satır).
  reset  : YIKICI. Bu projenin volume'larını siler. -ConfirmReset <proje adı> ister ve volume'ların bu ürüne ait olduğunu
           (dev.kulvar.stack etiketi) doğrular; etiket yoksa hiçbir şey silmez.
.EXAMPLE
  .\scripts\local-stack.ps1 init -SeedDemo
  .\scripts\local-stack.ps1 up -SeedDemo -Simulation
  .\scripts\local-stack.ps1 down
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('init', 'up', 'seed', 'down', 'status', 'logs', 'reset')] [string]$Action,
    [string]$Project = 'kulvar',
    [string]$LocalDir = '.local',
    [int]$Port = 8843,
    [switch]$SeedDemo,
    [switch]$Simulation,
    [string]$ConfirmReset = ''
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
$local = Join-Path $root $LocalDir
$demoUsers = 'ayse.owner', 'ali.admin', 'asli.agent', 'arda.viewer', 'burak.owner', 'belma.agent', 'cem.ortak', 'operator'

function New-Secret([int]$Bytes = 24) {
    $buffer = New-Object byte[] $Bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return ([Convert]::ToBase64String($buffer)).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
}

function Write-SecretFile([string]$Path, [string]$Value) {
    # Sonda satır sonu yok: key-per-file ve *_FILE okuyucuları değeri olduğu gibi alır.
    [System.IO.File]::WriteAllText($Path, $Value, (New-Object System.Text.UTF8Encoding($false)))
}

function Ensure-Secret([string]$Name, [scriptblock]$Factory) {
    $path = Join-Path $local "secrets\$Name"
    if (-not (Test-Path $path)) { Write-SecretFile $path (& $Factory) }
    return [System.IO.File]::ReadAllText($path)
}

function Find-Docker {
    $cmd = Get-Command docker -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidate = Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
    if (Test-Path $candidate) { return $candidate }
    throw 'docker CLI bulunamadı. Docker Desktop çalışıyor olmalı.'
}

function Set-ComposeEnvironment {
    $env:KULVAR_LOCAL_DIR = './' + ($LocalDir -replace '^\.[\\/]', '').Replace('\', '/')
    $env:KULVAR_HTTPS_PORT = "$Port"
    $env:KULVAR_SIMULATION = $(if ($Simulation) { 'true' } else { 'false' })
}

function Assert-Initialized {
    if (-not (Test-Path (Join-Path $local 'secrets\app_connection'))) { throw "Önce: .\scripts\local-stack.ps1 init -LocalDir $LocalDir" }
}

function Invoke-Seed([string]$Docker) {
    if (-not (Test-Path (Join-Path $local 'seed\Seed__Passwords__operator'))) { throw "Demo parolaları yok. Önce: .\scripts\local-stack.ps1 init -SeedDemo -LocalDir $LocalDir" }
    & $Docker compose -p $Project --profile seed run --rm seed
    if ($LASTEXITCODE -ne 0) { throw "Demo seed başarısız (exit $LASTEXITCODE). Ayrıntı: .\scripts\local-stack.ps1 logs" }
    Write-Host "Demo verisi hazır. Hesap/şirket eşlemesi README'de; parolalar $LocalDir\seed altında (özel)."
}

switch ($Action) {
    'init' {
        foreach ($dir in 'secrets', 'certs', 'seed') { New-Item -ItemType Directory -Force (Join-Path $local $dir) | Out-Null }
        Ensure-Secret 'postgres_superuser_password' { New-Secret } | Out-Null
        $owner = Ensure-Secret 'kulvar_owner_password' { New-Secret }
        $app = Ensure-Secret 'kulvar_app_password' { New-Secret }
        $platform = Ensure-Secret 'kulvar_platform_password' { New-Secret }
        $worker = Ensure-Secret 'kulvar_worker_password' { New-Secret }
        # Simüle sağlayıcı webhook HMAC sırrı; en az 32 karakter.
        Ensure-Secret 'webhook_secret' { New-Secret 36 } | Out-Null
        $common = 'Host=postgres;Port=5432;Database=kulvar;Timeout=5;Command Timeout=20;Gss Encryption Mode=Disable'
        Write-SecretFile (Join-Path $local 'secrets\migrator_connection') "$common;Username=kulvar_owner;Password=$owner"
        Write-SecretFile (Join-Path $local 'secrets\app_connection') "$common;Username=kulvar_app;Password=$app"
        Write-SecretFile (Join-Path $local 'secrets\platform_connection') "$common;Username=kulvar_platform;Password=$platform;Maximum Pool Size=10"
        Write-SecretFile (Join-Path $local 'secrets\worker_connection') "$common;Username=kulvar_worker;Password=$worker;Maximum Pool Size=10"

        $pfx = Join-Path $local 'certs\kulvar-dev.pfx'
        if (-not (Test-Path $pfx)) {
            $pfxPassword = Ensure-Secret 'https_certificate_password' { New-Secret }
            $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
            if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
            & $dotnet dev-certs https --export-path $pfx --password $pfxPassword --format Pfx | Out-Null
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $pfx)) { throw 'Geliştirici sertifikası dışa aktarılamadı (dotnet dev-certs https --check ile kontrol edin).' }
        }

        if ($SeedDemo) {
            foreach ($user in $demoUsers) {
                $path = Join-Path $local "seed\Seed__Passwords__$user"
                # Parola politikası: en az 12 karakter.
                if (-not (Test-Path $path)) { Write-SecretFile $path ('Kv!' + (New-Secret 15) + 'aA9') }
            }
        }
        Write-Host "Hazır: $LocalDir (secrets, certs$(if ($SeedDemo) { ', seed' })). Değerler ekrana yazılmadı."
    }
    'up' {
        $docker = Find-Docker
        Assert-Initialized
        Set-ComposeEnvironment
        & $docker compose -p $Project up -d --build --wait
        $upExit = $LASTEXITCODE
        if ($upExit -ne 0) { & $docker compose -p $Project ps -a; throw "compose up başarısız (exit $upExit)" }
        & $docker compose -p $Project ps
        if ($SeedDemo) { Invoke-Seed $docker }
        Write-Host "Kulvar: https://localhost:$Port"
    }
    'seed' {
        $docker = Find-Docker
        Assert-Initialized
        Set-ComposeEnvironment
        Invoke-Seed $docker
    }
    'down' {
        $docker = Find-Docker
        Set-ComposeEnvironment
        & $docker compose -p $Project --profile seed down --remove-orphans
        Write-Host 'Durduruldu. Volume''lar korundu (silmek için: reset -ConfirmReset <proje>).'
    }
    'status' {
        $docker = Find-Docker
        Set-ComposeEnvironment
        & $docker compose -p $Project ps -a
    }
    'logs' {
        $docker = Find-Docker
        Set-ComposeEnvironment
        & $docker compose -p $Project logs --tail 200
    }
    'reset' {
        $docker = Find-Docker
        if ($ConfirmReset -cne $Project) { throw "Yıkıcı işlem: onay için -ConfirmReset $Project verin. Hiçbir şey silinmedi." }
        # Hedef koruması: silinecek her volume bu ürünün etiketini taşımalı. Başka bir projenin adı verilirse hiçbir şey silinmez.
        $volumes = @(& $docker volume ls --filter "label=com.docker.compose.project=$Project" --format '{{.Name}}')
        foreach ($volume in $volumes) {
            # JSON olarak okunur: Windows PowerShell yerel komutlara giden iç çift tırnakları düşürdüğü için şablonda tırnak kullanılmaz.
            $labels = (& $docker volume inspect $volume --format '{{json .Labels}}') | ConvertFrom-Json
            $label = if ($labels) { $labels.'dev.kulvar.stack' } else { $null }
            if ($label -ne 'true') { throw "Volume '$volume' Kulvar etiketi taşımıyor; '$Project' bu ürünün yığını değil. Hiçbir şey silinmedi." }
        }
        Set-ComposeEnvironment
        & $docker compose -p $Project --profile seed down -v --remove-orphans
        Write-Host "Sıfırlandı: '$Project' yığınının $($volumes.Count) volume'u silindi. Yerel sırlar ($LocalDir) korunur."
    }
}
