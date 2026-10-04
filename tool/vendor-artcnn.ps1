param(
    [Parameter(Mandatory = $false)][string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$targetRoot = Join-Path $root "native\artcnn"
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

# ArtCNN v1.6.2 official real-time GLSL models.
# Git blob SHA-1 = SHA1("blob <byte-length>\0" + file-bytes), which lets us
# verify the exact files pinned by the Git tag rather than trusting only HTTPS.
$files = @(
    @{
        Name = "ArtCNN_C4F16.glsl"
        Url = "https://raw.githubusercontent.com/Artoriuz/ArtCNN/v1.6.2/GLSL/ArtCNN_C4F16.glsl"
        GitBlobSha1 = "4086dce92db6c1d9d81d3e396aa94d35a1e389a8"
    },
    @{
        Name = "ArtCNN_C4F32.glsl"
        Url = "https://raw.githubusercontent.com/Artoriuz/ArtCNN/v1.6.2/GLSL/ArtCNN_C4F32.glsl"
        GitBlobSha1 = "00a487233c1d77a35b7084d395efcbde21fbffef"
    }
)

function Get-GitBlobSha1 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    $prefix = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $all = New-Object byte[] ($prefix.Length + $bytes.Length)
    [Buffer]::BlockCopy($prefix, 0, $all, 0, $prefix.Length)
    [Buffer]::BlockCopy($bytes, 0, $all, $prefix.Length, $bytes.Length)

    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        return -join ($sha1.ComputeHash($all) | ForEach-Object { $_.ToString("x2") })
    }
    finally {
        $sha1.Dispose()
    }
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
catch {
    # PowerShell 7 / modern .NET may manage TLS itself.
}

foreach ($entry in $files) {
    $destination = Join-Path $targetRoot $entry.Name

    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $existingSha = Get-GitBlobSha1 -Path $destination
        if ($existingSha -eq $entry.GitBlobSha1) {
            Write-Host "ArtCNN hazır: $($entry.Name)" -ForegroundColor Green
            continue
        }

        Write-Warning "ArtCNN dosyası beklenen v1.6.2 içeriği değil; yeniden indirilecek: $($entry.Name)"
    }

    $temporary = Join-Path $env:TEMP ("altyazidb-artcnn-" + [Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        Write-Host "ArtCNN indiriliyor: $($entry.Name)" -ForegroundColor Cyan
        Invoke-WebRequest `
            -Uri $entry.Url `
            -OutFile $temporary `
            -UseBasicParsing `
            -Headers @{ "User-Agent" = "ADB-Player-Rev10.4" }

        $actualSha = Get-GitBlobSha1 -Path $temporary
        if ($actualSha -ne $entry.GitBlobSha1) {
            throw @"
ArtCNN kaynak doğrulaması başarısız: $($entry.Name)

Beklenen Git blob SHA-1:
$($entry.GitBlobSha1)

Bulunan:
$actualSha
"@
        }

        Move-Item -LiteralPath $temporary -Destination $destination -Force
        Write-Host "ArtCNN doğrulandı: $($entry.Name)" -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

$sourceInfo = @"
ArtCNN v1.6.2
Upstream: https://github.com/Artoriuz/ArtCNN
ArtCNN_C4F16.glsl git-blob: 4086dce92db6c1d9d81d3e396aa94d35a1e389a8
ArtCNN_C4F32.glsl git-blob: 00a487233c1d77a35b7084d395efcbde21fbffef
License: MIT
"@
[IO.File]::WriteAllText(
    (Join-Path $targetRoot "ARTCNN_SOURCE.txt"),
    $sourceInfo,
    (New-Object Text.UTF8Encoding($false)))

Write-Host "ArtCNN v1.6.2 runtime hazır." -ForegroundColor Green
