<#
.SYNOPSIS
  Kaynak parmak izi: test/doğrulama kayıtlarının hangi kaynak durumuna ait olduğunu gösterir.
.DESCRIPTION
  git'in izlediği ve yok saymadığı dosyalar (commit gerekmez) üzerinden, yol + içerik SHA-256'larının sıralı birleşiminin SHA-256'sı.
  Belgeler (docs/), doğrulama artefaktları (artifacts/) ve tasarım önizlemesi (design/) hariçtir: bunlar davranışı değiştirmez.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
$files = @(& git ls-files --cached --others --exclude-standard) |
    Where-Object { $_ -and $_ -notmatch '^(docs|artifacts|design)/' -and $_ -notmatch '^README\.md$' } |
    Sort-Object -Unique
$sha = [System.Security.Cryptography.SHA256]::Create()
$builder = New-Object System.Text.StringBuilder
foreach ($file in $files) {
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $hash = [BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($path))).Replace('-', '').ToLowerInvariant()
    [void]$builder.Append($file).Append(':').Append($hash).Append("`n")
}
$fingerprint = [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($builder.ToString()))).Replace('-', '').ToLowerInvariant()
Write-Output ("{0} files={1}" -f $fingerprint.Substring(0, 16), $files.Count)
