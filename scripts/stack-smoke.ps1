<#
.SYNOPSIS
  Yığın duman testi: kendi compose projesi (kulvar-smoke), kendi portu (8853) ve kendi yerel dizini (.local-smoke) ile çalışır.
  Geliştirme yığınına (kulvar) ve komşu projelere dokunmaz; sonunda yalnız kendi kaynaklarını temizler.
.DESCRIPTION
  Doğrulananlar: boş yığından kurulum, HTTPS üzerinden sağlık/SPA/API, demo seed ve seed'in yinelenebilirliği, yeniden başlatmada
  kalıcılık, veritabanı kesintisinde sahte başarı olmaması ve toparlanma, root olmayan/salt-okunur container'lar, yıkıcı reset koruması.
  HTTP istekleri curl.exe -k ile yapılır: geliştirici sertifikasının bu makinede güvenilir olduğu varsayılmaz.
#>
param(
    [string]$Project = 'kulvar-smoke',
    [string]$LocalDir = '.local-smoke',
    [int]$Port = 8853,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
if ($Project -eq 'kulvar' -or $LocalDir -eq '.local') { throw 'Duman testi geliştirme yığınına karşı çalıştırılmaz (ayrı proje/dizin verin).' }

$stack = Join-Path $PSScriptRoot 'local-stack.ps1'
$base = "https://localhost:$Port"
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("kulvar-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$results = New-Object System.Collections.Generic.List[object]
$started = Get-Date

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Check = $Name; Result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
    Write-Host ("[{0}] {1} {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
}

function Http([string]$Method, [string]$Path, [string]$Jar = '', [string]$Json = '', [string]$Csrf = '') {
    $out = Join-Path $work 'body.txt'
    $arguments = @('-k', '-s', '--max-time', '25', '-o', $out, '-w', '%{http_code}', '-X', $Method)
    if ($Jar) { $arguments += @('-b', $Jar, '-c', $Jar) }
    if ($Csrf) { $arguments += @('-H', "X-CSRF-TOKEN: $Csrf") }
    if ($Json) {
        $payload = Join-Path $work 'payload.json'
        [System.IO.File]::WriteAllText($payload, $Json, (New-Object System.Text.UTF8Encoding($false)))
        $arguments += @('-H', 'Content-Type: application/json', '--data-binary', "@$payload")
    }
    $arguments += "$base$Path"
    $status = & curl.exe @arguments
    $body = if (Test-Path $out) { [System.IO.File]::ReadAllText($out) } else { '' }
    return [pscustomobject]@{ Status = [int]$status; Body = $body }
}

function Get-Csrf([string]$Jar) { return ((Http 'GET' '/api/auth/csrf' $Jar).Body | ConvertFrom-Json).token }

function Login([string]$UserKey, [string]$Email) {
    $jar = Join-Path $work "$UserKey.jar"
    if (Test-Path $jar) { Remove-Item $jar -Force }
    $password = [System.IO.File]::ReadAllText((Join-Path $root "$LocalDir\seed\Seed__Passwords__$UserKey"))
    $body = (@{ email = $Email; password = $password } | ConvertTo-Json -Compress)
    $response = Http 'POST' '/api/auth/login' $jar $body (Get-Csrf $jar)
    return [pscustomobject]@{ Status = $response.Status; Jar = $jar }
}

function Sql([string]$Query) {
    return (& docker compose -p $Project exec -T postgres psql -U postgres -d kulvar -At -c $Query) -join "`n"
}

function Wait-Status([string]$Path, [int]$Expected, [int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $last = (Http 'GET' $Path).Status
        if ($last -eq $Expected) { return $true }
        Start-Sleep -Milliseconds 1000
    } while ((Get-Date) -lt $deadline)
    return $false
}

$env:KULVAR_LOCAL_DIR = "./$LocalDir"; $env:KULVAR_HTTPS_PORT = "$Port"; $env:KULVAR_SIMULATION = 'false'
$foreignVolumesBefore = @(& docker volume ls --format '{{.Name}}' | Where-Object { $_ -notlike "$Project*" } | Sort-Object)
$foreignContainersBefore = @(& docker ps --format '{{.Names}}|{{.Status}}' | Where-Object { $_ -notlike "$Project*" } | ForEach-Object { ($_ -split '\|')[0] } | Sort-Object)

try {
    # 0) Temiz başlangıç: yalnız bu duman projesinin önceki kalıntıları (etiket korumalı reset).
    & $stack reset -Project $Project -LocalDir $LocalDir -Port $Port -ConfirmReset $Project | Out-Null
    & $stack init -Project $Project -LocalDir $LocalDir -Port $Port -SeedDemo | Out-Null

    # 1) Boş yığından kurulum + demo seed
    & $stack up -Project $Project -LocalDir $LocalDir -Port $Port -SeedDemo | Out-Null
    Check 'Boş yığın ayağa kalktı ve seed tamamlandı' $true
    Check 'GET /health/live = 200' ((Http 'GET' '/health/live').Status -eq 200)
    Check 'GET /health/ready = 200' ((Http 'GET' '/health/ready').Status -eq 200)
    $spa = Http 'GET' '/'
    Check 'SPA aynı origin''den sunuluyor' ($spa.Status -eq 200 -and $spa.Body -match '<div id="root">')
    $anonymous = Http 'GET' '/api/me'
    $license = Http 'GET' '/licenses/IBM-Plex-Sans-OFL-1.1.txt'
    Check 'Gömülü yazı tipinin lisans metni uygulamayla sunuluyor' ($license.Status -eq 200 -and $license.Body -match 'SIL Open Font License')
    Check 'Oturumsuz /api/me = 401 problem+json' ($anonymous.Status -eq 401 -and $anonymous.Body -match '"code":"unauthenticated"')
    $headers = (& curl.exe -k -s -D - -o NUL "$base/api/plans") -join "`n"
    Check 'Güvenlik başlıkları (CSP, nosniff, HSTS)' ($headers -match 'Content-Security-Policy' -and $headers -match 'X-Content-Type-Options: nosniff' -and $headers -match 'Strict-Transport-Security')

    $owner = Login 'ayse.owner' 'ayse.owner@akasya.test'
    Check 'Demo Owner girişi (seed parolası özel dosyadan)' ($owner.Status -eq 200)
    $memberships = (Http 'GET' '/api/me/memberships' $owner.Jar).Body | ConvertFrom-Json
    Check 'Demo şirketi üyeliklerde görünüyor' (@($memberships.items | Where-Object { $_.tenantName -eq 'Akasya Mobilya' -and $_.role -eq 'Owner' }).Count -eq 1)
    $operator = Login 'operator' 'operator@kulvar.test'
    $me = (Http 'GET' '/api/me' $operator.Jar).Body | ConvertFrom-Json
    Check 'Platform operatörü kurulum komutuyla atanmış' ($operator.Status -eq 200 -and $me.isPlatformOperator -eq $true)

    # 2) Seed yinelenebilir
    $snapshotQuery = "SELECT (SELECT count(*) FROM app.users) || ':' || (SELECT count(*) FROM app.tenants) || ':' || (SELECT count(*) FROM app.memberships) || ':' || (SELECT count(*) FROM app.platform_operators)"
    $before = Sql $snapshotQuery
    & $stack seed -Project $Project -LocalDir $LocalDir -Port $Port | Out-Null
    $after = Sql $snapshotQuery
    Check 'Seed yinelenebilir (ikinci koşu satır eklemedi)' ($before -eq $after -and $before -match '^\d+:\d+:\d+:\d+$') "kullanıcı:şirket:üyelik:operatör = $after"

    # 3) Yeniden başlatmada kalıcılık (container'lar silinir, volume'lar kalır)
    & $stack down -Project $Project -LocalDir $LocalDir -Port $Port | Out-Null
    & $stack up -Project $Project -LocalDir $LocalDir -Port $Port | Out-Null
    Check 'Yeniden başlatma sonrası veri duruyor' ((Sql $snapshotQuery) -eq $after)
    $again = Login 'ayse.owner' 'ayse.owner@akasya.test'
    Check 'Yeniden başlatma sonrası giriş çalışıyor' ($again.Status -eq 200)
    Check 'Oturum cookie''si yeniden başlatmadan sonra da çözülüyor (Data Protection anahtarları kalıcı)' ((Http 'GET' '/api/me' $again.Jar).Status -eq 200)

    # 4) Veritabanı kesintisi: sahte başarı yok; toparlanma
    & docker compose -p $Project stop postgres | Out-Null
    Check 'DB kapalı: /health/ready = 503' (Wait-Status '/health/ready' 503 40)
    Check 'DB kapalı: /health/live = 200' ((Http 'GET' '/health/live').Status -eq 200)
    $downMe = Http 'GET' '/api/me' $again.Jar
    Check 'DB kapalı: oturumlu istek 503 (200 değil)' ($downMe.Status -eq 503 -and $downMe.Body -match '"code":"dependency_unavailable"')
    $downLogin = Login 'ayse.owner' 'ayse.owner@akasya.test'
    Check 'DB kapalı: giriş 503 (200/401 değil)' ($downLogin.Status -eq 503)
    & docker compose -p $Project start postgres | Out-Null
    Check 'DB açıldı: /health/ready = 200' (Wait-Status '/health/ready' 200 90)
    Check 'DB açıldı: giriş yeniden çalışıyor' ((Login 'ayse.owner' 'ayse.owner@akasya.test').Status -eq 200)
    Check 'DB açıldı: veri duruyor' ((Sql $snapshotQuery) -eq $after)

    # 5) En az yetki: container'lar
    foreach ($service in 'api', 'worker') {
        $container = (& docker compose -p $Project ps -q $service)
        $info = & docker inspect $container --format '{{.Config.User}}|{{.HostConfig.ReadonlyRootfs}}|{{.HostConfig.CapDrop}}|{{.HostConfig.SecurityOpt}}'
        $uid = (& docker compose -p $Project exec -T $service id -u)
        Check "$service root değil, kök dosya sistemi salt-okunur, yetenekler bırakılmış" ($uid -ne '0' -and $info -match '\|true\|' -and $info -match 'ALL' -and $info -match 'no-new-privileges') "uid=$uid $info"
    }
    $workerHealth = & docker inspect (& docker compose -p $Project ps -q worker) --format '{{.State.Health.Status}}'
    Check 'Worker sağlıklı (heartbeat)' ($workerHealth -eq 'healthy') $workerHealth
    # İmajın kendisinde (secret bağlamaları olmadan) sır/sertifika/demo parolası dosyası ve build geçmişinde sır yok.
    $imageId = & docker inspect (& docker compose -p $Project ps -q api) --format '{{.Image}}'
    $find = "find / -xdev -type f \( -name '*.pfx' -o -name '*.pem' -o -name '*.key' -o -name '*_password' -o -name '*_connection' -o -name 'webhook_secret' -o -name 'Seed__Passwords__*' -o -name '.env' \) -not -path '/proc/*' -not -path '/sys/*' -not -path '/etc/ssl/*' -not -path '/usr/*' | wc -l"
    # root olarak aranır (yetkisiz kullanıcının göremediği dizinler dahil); pozitif kontrol: aynı arama uygulama DLL'ini bulmalı.
    $control = "find / -xdev -type f -name 'Kulvar.Api.dll' -not -path '/proc/*' | wc -l"
    $secretFiles = "$(& docker run --rm --network none --user 0 --entrypoint /bin/sh $imageId -c $find | Select-Object -Last 1)".Trim()
    $controlFiles = "$(& docker run --rm --network none --user 0 --entrypoint /bin/sh $imageId -c $control | Select-Object -Last 1)".Trim()
    $history = (& docker history --no-trunc --format '{{.CreatedBy}}' $imageId) -join "`n"
    Check 'İmajda sır/sertifika/demo parolası dosyası yok; build geçmişinde sır yok' ($secretFiles -eq '0' -and $controlFiles -eq '1' -and $history -notmatch '(?i)password=|_password|webhook_secret') "dosya=$secretFiles kontrol=$controlFiles"
    $roles = Sql "SELECT string_agg(rolname || '=' || rolsuper::text || '/' || rolbypassrls::text, ',' ORDER BY rolname) FROM pg_roles WHERE rolname LIKE 'kulvar\_%'"
    Check 'Runtime rolleri superuser/BYPASSRLS değil' ($roles -notmatch 'true' -and $roles -match 'kulvar_app=false/false') $roles
    $published = (& docker compose -p $Project port api 8843)
    Check 'API yalnız 127.0.0.1''de yayımlanıyor' ($published -like '127.0.0.1:*') $published
    Check 'PostgreSQL portu dışarı açılmamış' ([string]::IsNullOrWhiteSpace((& docker port (& docker compose -p $Project ps -q postgres) 2>$null)))

    # 6) Yıkıcı reset koruması: Kulvar etiketi taşımayan (yabancı) volume'a dokunulmaz. Gerçek komşu projeler yerine atılabilir bir sahte kullanılır.
    $foreign = 'kulvar-foreign-guard-test'
    & docker volume create --label "com.docker.compose.project=$foreign" "${foreign}_pgdata" | Out-Null
    $refused = $false
    try { & $stack reset -Project $foreign -LocalDir $LocalDir -Port $Port -ConfirmReset $foreign | Out-Null } catch { $refused = $true }
    $stillThere = @(& docker volume ls --format '{{.Name}}' | Where-Object { $_ -eq "${foreign}_pgdata" }).Count -eq 1
    & docker volume rm "${foreign}_pgdata" | Out-Null
    Check 'reset: etiketsiz (yabancı) volume silinmedi' ($refused -and $stillThere)
    $unconfirmed = $false
    try { & $stack reset -Project $Project -LocalDir $LocalDir -Port $Port | Out-Null } catch { $unconfirmed = $true }
    Check 'reset: onaysız çağrı hiçbir şey silmedi' ($unconfirmed -and ((Sql $snapshotQuery) -eq $after))
}
catch {
    Check 'Beklenmeyen hata' $false $_.Exception.Message
}
finally {
    if (-not $KeepRunning) {
        try { & $stack reset -Project $Project -LocalDir $LocalDir -Port $Port -ConfirmReset $Project | Out-Null } catch { Write-Host "Temizlik uyarısı: $($_.Exception.Message)" }
        $localPath = Join-Path $root $LocalDir
        # Yalnız bu betiğin kendi dizini silinir.
        if ($LocalDir -like '.local-*' -and (Test-Path $localPath)) { Remove-Item -Recurse -Force $localPath }
    }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

$foreignVolumesAfter = @(& docker volume ls --format '{{.Name}}' | Where-Object { $_ -notlike "$Project*" } | Sort-Object)
$foreignContainersAfter = @(& docker ps --format '{{.Names}}|{{.Status}}' | Where-Object { $_ -notlike "$Project*" } | ForEach-Object { ($_ -split '\|')[0] } | Sort-Object)
Check 'Başka projelerin volume''ları değişmedi' (($foreignVolumesBefore -join ',') -eq ($foreignVolumesAfter -join ',')) ($foreignVolumesAfter -join ', ')
Check 'Başka projelerin çalışan container''ları değişmedi' (($foreignContainersBefore -join ',') -eq ($foreignContainersAfter -join ',')) ($foreignContainersAfter -join ', ')
if (-not $KeepRunning) {
    $leftover = @(& docker ps -a --format '{{.Names}}' | Where-Object { $_ -like "$Project-*" }) + @(& docker volume ls --format '{{.Name}}' | Where-Object { $_ -like "${Project}_*" })
    Check 'Duman yığını temizlendi (container/volume kalmadı)' ($leftover.Count -eq 0) ($leftover -join ', ')
}

$failed = @($results | Where-Object { $_.Result -eq 'FAIL' }).Count
$summary = "Yığın duman testi: $($results.Count) kontrol, $failed başarısız, süre $([int]((Get-Date) - $started).TotalSeconds) sn"
Write-Host $summary
$logDir = Join-Path $root 'artifacts\verification'
New-Item -ItemType Directory -Force $logDir | Out-Null
$lines = @("# $summary", "# tarih: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) proje=$Project port=$Port") + ($results | ForEach-Object { "{0}`t{1}`t{2}" -f $_.Result, $_.Check, $_.Detail })
[System.IO.File]::WriteAllLines((Join-Path $logDir 'stack-smoke.log'), $lines, (New-Object System.Text.UTF8Encoding($false)))
exit $(if ($failed -eq 0) { 0 } else { 1 })
