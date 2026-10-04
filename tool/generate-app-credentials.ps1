param(
    [string]$EnvFile
)

. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    # Optional shared/legacy credential file override. This lets developers keep
    # one secret file outside the repo and reuse it across working copies.
    $sharedEnv = [Environment]::GetEnvironmentVariable('ADB_PLAYER_ENV_FILE', [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($sharedEnv)) {
        $sharedEnv = [Environment]::GetEnvironmentVariable('ADB_PLAYER_ENV_FILE', [EnvironmentVariableTarget]::User)
    }
    if ([string]::IsNullOrWhiteSpace($sharedEnv)) {
        $sharedEnv = [Environment]::GetEnvironmentVariable('ADB_PLAYER_ENV_FILE', [EnvironmentVariableTarget]::Machine)
    }

    $localEnv = Join-Path $root ".env.local"
    $legacyEnv = Join-Path $root ".env"

    if (-not [string]::IsNullOrWhiteSpace($sharedEnv)) {
        if (-not (Test-Path -LiteralPath $sharedEnv)) {
            throw "ADB_PLAYER_ENV_FILE is set but the file does not exist: $sharedEnv"
        }
        $EnvFile = $sharedEnv
    }
    elseif (Test-Path -LiteralPath $localEnv) {
        $EnvFile = $localEnv
    }
    elseif (Test-Path -LiteralPath $legacyEnv) {
        $EnvFile = $legacyEnv
    }
    else {
        $EnvFile = $localEnv
    }
}
$cloudOutput = Join-Path $root "src\AltyaziDB.Player.Cloud\Generated\GeneratedAppCredentials.g.cs"
$telegramOutput = Join-Path $root "src\AltyaziDB.Player.Telegram\Generated\GeneratedTelegramCredentials.g.cs"
$subtitleOutput = Join-Path $root "src\AltyaziDB.Player.Subtitles\Generated\GeneratedAltyaziDbCredentials.g.cs"
$adbCloudOutput = Join-Path $root "src\AltyaziDB.Player.Infrastructure\Generated\GeneratedAdbCloudConfig.g.cs"

$values = @{}
if (Test-Path -LiteralPath $EnvFile) {
    foreach ($line in Get-Content -LiteralPath $EnvFile -Encoding UTF8) {
        $text = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($text) -or $text.StartsWith('#')) { continue }
        $index = $text.IndexOf('=')
        if ($index -lt 1) { continue }
        $key = $text.Substring(0, $index).Trim()
        $value = $text.Substring($index + 1).Trim()
        if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$key] = $value
    }
}

function Escape-CSharp([string]$Value) {
    if ($null -eq $Value) { return '' }
    return $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n')
}

function Get-EnvValue([string]$Name) {
    if ($values.ContainsKey($Name)) { return [string]$values[$Name] }

    # .env.local is convenient for repo-local development, but developer machines
    # may already keep credentials in environment variables. Honor them without
    # writing secrets back to disk or source control.
    $processValue = [Environment]::GetEnvironmentVariable($Name, [EnvironmentVariableTarget]::Process)
    if (-not [string]::IsNullOrWhiteSpace($processValue)) { return [string]$processValue }

    $userValue = [Environment]::GetEnvironmentVariable($Name, [EnvironmentVariableTarget]::User)
    if (-not [string]::IsNullOrWhiteSpace($userValue)) { return [string]$userValue }

    $machineValue = [Environment]::GetEnvironmentVariable($Name, [EnvironmentVariableTarget]::Machine)
    if (-not [string]::IsNullOrWhiteSpace($machineValue)) { return [string]$machineValue }

    return ''
}

function Get-EnvValueAny([string[]]$Names) {
    foreach ($name in $Names) {
        $value = Get-EnvValue $name
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }
    return ''
}

$cloudMap = [ordered]@{
    GoogleClientId = @('GOOGLE_CLIENT_ID', 'GOOGLE_SERVER_CLIENT_ID')
    GoogleClientSecret = @('GOOGLE_CLIENT_SECRET')
    DropboxClientId = @('DROPBOX_CLIENT_ID', 'DROPBOX_APP_KEY')
    DropboxClientSecret = @('DROPBOX_CLIENT_SECRET')
    OneDriveClientId = @('ONEDRIVE_CLIENT_ID')
    OneDriveClientSecret = @('ONEDRIVE_CLIENT_SECRET')
    PCloudClientId = @('PCLOUD_CLIENT_ID')
    PCloudClientSecret = @('PCLOUD_CLIENT_SECRET')
}

$cloudLines = New-Object System.Collections.Generic.List[string]
$cloudLines.Add('// Bu dosya otomatik üretilmiştir. Gerçek değerleri kaynak kontrolüne eklemeyin.')
$cloudLines.Add('namespace AltyaziDB.Player.Cloud.Generated;')
$cloudLines.Add('')
$cloudLines.Add('internal static class GeneratedAppCredentials')
$cloudLines.Add('{')
foreach ($pair in $cloudMap.GetEnumerator()) {
    $value = Get-EnvValueAny $pair.Value
    $escaped = Escape-CSharp $value
    $cloudLines.Add("    internal const string $($pair.Key) = `"$escaped`";")
}
$cloudLines.Add('}')

$subtitleApiUrl = Escape-CSharp (Get-EnvValue 'ALTYAZIDB_API_URL')
if ([string]::IsNullOrWhiteSpace($subtitleApiUrl)) {
    $subtitleApiUrl = 'https://altyazidb.com/api/v1'
}
$subtitleApiKey = Escape-CSharp (Get-EnvValue 'ALTYAZIDB_API_KEY')

$subtitleLines = New-Object System.Collections.Generic.List[string]
$subtitleLines.Add('// Bu dosya otomatik üretilmiştir. Gerçek değerleri kaynak kontrolüne eklemeyin.')
$subtitleLines.Add('namespace AltyaziDB.Player.Subtitles.Generated;')
$subtitleLines.Add('')
$subtitleLines.Add('public static class GeneratedAltyaziDbCredentials')
$subtitleLines.Add('{')
$subtitleLines.Add("    public const string ApiUrl = `"$subtitleApiUrl`";")
$subtitleLines.Add("    public const string ApiKey = `"$subtitleApiKey`";")
$subtitleLines.Add('}')

$telegramApiId = 0
$telegramApiIdText = Get-EnvValue 'TELEGRAM_API_ID'
if (-not [string]::IsNullOrWhiteSpace($telegramApiIdText)) {
    $parsedApiId = 0
    if (-not [int]::TryParse($telegramApiIdText, [ref]$parsedApiId) -or $parsedApiId -le 0) {
        throw "TELEGRAM_API_ID pozitif bir tam sayı olmalıdır."
    }
    $telegramApiId = $parsedApiId
}
$telegramApiHash = Escape-CSharp (Get-EnvValue 'TELEGRAM_API_HASH')

$telegramLines = New-Object System.Collections.Generic.List[string]
$telegramLines.Add('// Bu dosya otomatik üretilmiştir. Gerçek değerleri kaynak kontrolüne eklemeyin.')
$telegramLines.Add('namespace AltyaziDB.Player.Telegram.Generated;')
$telegramLines.Add('')
$telegramLines.Add('public static class GeneratedTelegramCredentials')
$telegramLines.Add('{')
$telegramLines.Add("    public const int ApiId = $telegramApiId;")
$telegramLines.Add("    public const string ApiHash = `"$telegramApiHash`";")
$telegramLines.Add('}')

$adbCloudAuthUrl = Escape-CSharp (Get-EnvValue 'ADB_CLOUD_AUTH_URL')
$adbCloudApiUrl = Escape-CSharp (Get-EnvValue 'ADB_CLOUD_API_URL')
$adbCloudLines = New-Object System.Collections.Generic.List[string]
$adbCloudLines.Add('// Bu dosya otomatik üretilmiştir. ADB Cloud Auth endpoint gizli değildir.')
$adbCloudLines.Add('namespace AltyaziDB.Player.Infrastructure.Generated;')
$adbCloudLines.Add('')
$adbCloudLines.Add('public static class GeneratedAdbCloudConfig')
$adbCloudLines.Add('{')
$adbCloudLines.Add("    public const string AuthBaseUrl = `"$adbCloudAuthUrl`";")
$adbCloudLines.Add("    public const string ApiBaseUrl = `"$adbCloudApiUrl`";")
$adbCloudLines.Add('}')

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
New-Item -ItemType Directory -Path (Split-Path -Parent $cloudOutput) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $telegramOutput) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $subtitleOutput) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $adbCloudOutput) -Force | Out-Null
[System.IO.File]::WriteAllLines($cloudOutput, $cloudLines, $utf8Bom)
[System.IO.File]::WriteAllLines($telegramOutput, $telegramLines, $utf8Bom)
[System.IO.File]::WriteAllLines($subtitleOutput, $subtitleLines, $utf8Bom)
[System.IO.File]::WriteAllLines($adbCloudOutput, $adbCloudLines, $utf8Bom)

if (Test-Path -LiteralPath $EnvFile) {
    Write-Host "Kimlik dosyası: $EnvFile" -ForegroundColor DarkGray
    Write-Host "Uygulama kimlik bilgileri $([System.IO.Path]::GetFileName($EnvFile)) / ortam değişkenleri üzerinden üretildi." -ForegroundColor Green
} else {
    Write-Host ".env.local veya .env bulunamadı; varsa Windows/Process ortam değişkenleri kullanılacak." -ForegroundColor Yellow
}

if ($telegramApiId -gt 0 -and -not [string]::IsNullOrWhiteSpace($telegramApiHash)) {
    Write-Host "Telegram uygulama kimliği: hazır" -ForegroundColor Green
} else {
    Write-Host "Telegram uygulama kimliği eksik: TELEGRAM_API_ID ve TELEGRAM_API_HASH tanımlanmalı." -ForegroundColor Yellow
}
Write-Host "Bulut kimlik dosyası: $cloudOutput" -ForegroundColor DarkGray
Write-Host "Telegram kimlik dosyası: $telegramOutput" -ForegroundColor DarkGray
Write-Host "AltyazıDB API kimlik dosyası: $subtitleOutput" -ForegroundColor DarkGray
if (-not [string]::IsNullOrWhiteSpace($adbCloudAuthUrl)) {
    Write-Host "ADB Cloud Auth endpoint: hazır" -ForegroundColor Green
} else {
    Write-Host "ADB Cloud Auth endpoint tanımlı değil; hesap özelliği bağlantı kurulana kadar pasif kalacak." -ForegroundColor Yellow
}
if (-not [string]::IsNullOrWhiteSpace($adbCloudApiUrl)) {
    Write-Host "ADB Cloud API endpoint: hazır" -ForegroundColor Green
} else {
    Write-Host "ADB Cloud API endpoint tanımlı değil; profil ve cihaz senkronu pasif kalacak." -ForegroundColor Yellow
}
Write-Host "ADB Cloud yapılandırma dosyası: $adbCloudOutput" -ForegroundColor DarkGray
