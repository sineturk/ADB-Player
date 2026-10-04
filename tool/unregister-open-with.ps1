$ErrorActionPreference = "Stop"

$progIds = @("ADB.Player.Media", "AltyaziDB.Player.Media")
$extensions = @(
    ".mkv", ".mp4", ".avi", ".webm", ".mov", ".m4v", ".ts", ".m2ts", ".mts",
    ".mpeg", ".mpg", ".wmv", ".flv", ".ogv", ".vob", ".3gp", ".torrent"
)

foreach ($extension in $extensions) {
    $openWith = "HKCU:\Software\Classes\$extension\OpenWithProgids"
    if (Test-Path -LiteralPath $openWith) {
        $progIds | ForEach-Object { Remove-ItemProperty -Path $openWith -Name $_ -ErrorAction SilentlyContinue }
    }
}

$torrentKey = Get-Item -LiteralPath "HKCU:\Software\Classes\.torrent" -ErrorAction SilentlyContinue
if ($torrentKey -and $torrentKey.GetValue("") -in $progIds) {
    Set-Item -Path "HKCU:\Software\Classes\.torrent" -Value ""
}
Remove-Item -LiteralPath "HKCU:\Software\Classes\magnet" -Recurse -Force -ErrorAction SilentlyContinue
$progIds | ForEach-Object { Remove-Item -LiteralPath "HKCU:\Software\Classes\$_" -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "ADB Player dosya ve protokol kayıtları kaldırıldı." -ForegroundColor Green
