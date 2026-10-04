param(
    [string]$ExecutablePath,
    [switch]$RegisterTorrent,
    [switch]$RegisterMagnet
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
if (-not $ExecutablePath) {
    $candidate = Get-LatestDebugExe -BinRoot (Join-Path $root "src\AltyaziDB.Player.App\bin")
    if ($candidate) { $ExecutablePath = $candidate.FullName }
    else {
        $portable = Join-Path $root "artifacts\portable\ADB.Player.exe"
        if (Test-Path -LiteralPath $portable) { $ExecutablePath = $portable }
    }
}

if (-not $ExecutablePath -or -not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "ADB.Player.exe bulunamadı. Önce uygulamayı derleyin veya -ExecutablePath verin."
}

$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$progId = "ADB.Player.Media"
$legacyProgId = "AltyaziDB.Player.Media"
$progRoot = "HKCU:\Software\Classes\$progId"
$command = "`"$ExecutablePath`" `"%1`""

New-Item -Path $progRoot -Force | Out-Null
Set-Item -Path $progRoot -Value "ADB Player medya dosyası"
New-Item -Path "$progRoot\DefaultIcon" -Force | Out-Null
Set-Item -Path "$progRoot\DefaultIcon" -Value "`"$ExecutablePath`",0"
New-Item -Path "$progRoot\shell\open\command" -Force | Out-Null
Set-Item -Path "$progRoot\shell\open\command" -Value $command

$extensions = @(
    ".mkv", ".mp4", ".avi", ".webm", ".mov", ".m4v", ".ts", ".m2ts", ".mts",
    ".mpeg", ".mpg", ".wmv", ".flv", ".ogv", ".vob", ".3gp"
)
if ($RegisterTorrent) { $extensions += ".torrent" }

Remove-Item -LiteralPath "HKCU:\Software\Classes\$legacyProgId" -Recurse -Force -ErrorAction SilentlyContinue

foreach ($extension in $extensions) {
    $openWith = "HKCU:\Software\Classes\$extension\OpenWithProgids"
    New-Item -Path $openWith -Force | Out-Null
    Remove-ItemProperty -Path $openWith -Name $legacyProgId -ErrorAction SilentlyContinue
    New-ItemProperty -Path $openWith -Name $progId -PropertyType String -Value "" -Force | Out-Null
}

if ($RegisterTorrent) {
    New-Item -Path "HKCU:\Software\Classes\.torrent" -Force | Out-Null
    Set-Item -Path "HKCU:\Software\Classes\.torrent" -Value $progId
}

if ($RegisterMagnet) {
    $magnet = "HKCU:\Software\Classes\magnet"
    New-Item -Path $magnet -Force | Out-Null
    Set-Item -Path $magnet -Value "URL:Magnet Protocol"
    New-ItemProperty -Path $magnet -Name "URL Protocol" -PropertyType String -Value "" -Force | Out-Null
    New-Item -Path "$magnet\DefaultIcon" -Force | Out-Null
    Set-Item -Path "$magnet\DefaultIcon" -Value "`"$ExecutablePath`",0"
    New-Item -Path "$magnet\shell\open\command" -Force | Out-Null
    Set-Item -Path "$magnet\shell\open\command" -Value $command
}

Write-Host "ADB Player Windows 'Birlikte aç' listesine kaydedildi." -ForegroundColor Green
Write-Host "EXE: $ExecutablePath"
if ($RegisterTorrent) { Write-Host ".torrent ilişkilendirmesi etkinleştirildi." -ForegroundColor Yellow }
if ($RegisterMagnet) { Write-Host "magnet protokolü ilişkilendirmesi etkinleştirildi." -ForegroundColor Yellow }
