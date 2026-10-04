[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repository = "bloc97/Anime4K"
$commit = "7684e9586f8dcc738af08a1cdceb024cc184f426"
$destination = Join-Path $ProjectRoot "native\anime4k"
New-Item -ItemType Directory -Path $destination -Force | Out-Null

# Dosyalar sabitlenmiş Anime4K commit'indeki Git blob kimlikleriyle indirilir.
# Böylece depo dalı değişse bile uygulamanın kullandığı shader içeriği değişmez.
$files = @(
    @{ Name = "Anime4K_Clamp_Highlights.glsl"; Sha = "71dcf7344a7757c82867949164c650095a7b9735" },
    @{ Name = "Anime4K_Restore_CNN_M.glsl"; Sha = "7f5ea9d87da38b7db82a5e115c5fd0980ed72f5e" },
    @{ Name = "Anime4K_Restore_CNN_Soft_M.glsl"; Sha = "2da72cb2bac3a62a53b7ccea51d0189e93eebaed" },
    @{ Name = "Anime4K_Upscale_CNN_x2_M.glsl"; Sha = "156c6bdd3b80a6c784ddf6655493bb35337cc0ec" },
    @{ Name = "Anime4K_Upscale_CNN_x2_S.glsl"; Sha = "e6ad7c218452014eaf70170cb5160e9ff427ffbe" },
    @{ Name = "Anime4K_AutoDownscalePre_x2.glsl"; Sha = "3e381373dc531160a0c876bc1a5675738fdb0cce" },
    @{ Name = "Anime4K_AutoDownscalePre_x4.glsl"; Sha = "1c4d421b2e4c42902b23ea62c18f8bdaaee280c7" },
    @{ Name = "Anime4K_Upscale_Denoise_CNN_x2_M.glsl"; Sha = "5076f5dd968ef854b48478f4547a411c811c37dc" }
)

function Get-GitBlobSha1([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $prefix = [System.Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $stream = New-Object System.IO.MemoryStream
        try {
            $stream.Write($prefix, 0, $prefix.Length)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Position = 0
            return ([System.BitConverter]::ToString($sha1.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
        }
        finally { $stream.Dispose() }
    }
    finally { $sha1.Dispose() }
}

$headers = @{
    "Accept" = "application/vnd.github+json"
    "User-Agent" = "ADB-Player-Anime4K-Vendor"
    "X-GitHub-Api-Version" = "2022-11-28"
}

$downloaded = 0
foreach ($file in $files) {
    $target = Join-Path $destination $file.Name
    if (-not $Force -and (Test-Path -LiteralPath $target -PathType Leaf)) {
        if ((Get-GitBlobSha1 $target) -eq $file.Sha) {
            Write-Host "Anime4K hazır: $($file.Name)" -ForegroundColor DarkGray
            continue
        }
        Remove-Item -LiteralPath $target -Force
    }

    $temporary = "$target.download"
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }

    $url = "https://api.github.com/repos/$repository/git/blobs/$($file.Sha)"
    Write-Host "Anime4K indiriliyor: $($file.Name)" -ForegroundColor Cyan
    $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get
    if ($response.encoding -ne "base64" -or [string]::IsNullOrWhiteSpace([string]$response.content)) {
        throw "Anime4K GitHub cevabı geçersiz: $($file.Name)"
    }
    if (-not [string]::Equals([string]$response.sha, [string]$file.Sha, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Anime4K Git blob kimliği eşleşmedi: $($file.Name)"
    }

    $base64 = ([string]$response.content) -replace "\s", ""
    [System.IO.File]::WriteAllBytes($temporary, [System.Convert]::FromBase64String($base64))
    $actual = Get-GitBlobSha1 $temporary
    if ($actual -ne $file.Sha) {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        throw "Anime4K dosya doğrulaması başarısız: $($file.Name)`nBeklenen Git blob: $($file.Sha)`nBulunan: $actual"
    }
    Move-Item -LiteralPath $temporary -Destination $target -Force
    $downloaded++
}

$sourceFile = Join-Path $destination "SOURCE.txt"
if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
    @"
Anime4K GLSL shaders
Repository: https://github.com/$repository
Pinned commit: $commit
Integration: mpv glsl-shaders property
The shader files are fetched from GitHub's blob API and verified against their pinned Git blob SHA-1 identifiers.
"@ | Set-Content -LiteralPath $sourceFile -Encoding UTF8
}

Write-Host "Anime4K shader kurulumu tamamlandı. Yeni indirilen: $downloaded" -ForegroundColor Green
