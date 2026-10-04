$ErrorActionPreference = "Stop"

try {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8
    [Console]::OutputEncoding = $utf8
    $global:OutputEncoding = $utf8
    if ($Host.Name -eq "ConsoleHost") { & chcp.com 65001 *> $null }
} catch {
    # Kodlama ayarı desteklenmiyorsa işlemi engelleme.
}

$script:AltyaziDbProjectRoot = Split-Path -Parent $PSScriptRoot

function Get-ProjectRoot { return $script:AltyaziDbProjectRoot }

function Get-DotNetExecutable {
    $preferred = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $preferred) { return $preferred }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw ".NET SDK bulunamadı. .NET 10 SDK x64 yükleyin." }
    return $command.Source
}

function Get-NuGetConfigPath {
    $config = Join-Path (Get-ProjectRoot) "NuGet.Config"
    if (-not (Test-Path -LiteralPath $config)) { throw "Proje NuGet.Config dosyası bulunamadı: $config" }
    return $config
}

function Get-ReleaseVersion {
    $props = Join-Path (Get-ProjectRoot) "Directory.Build.props"
    [xml]$xml = Get-Content -LiteralPath $props -Raw
    $value = [string]$xml.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Directory.Build.props içinde Version bulunamadı." }
    return $value.Trim()
}

function Get-CentralPackageVersion {
    param([Parameter(Mandatory=$true)][string]$PackageId)
    $props = Join-Path (Get-ProjectRoot) "Directory.Packages.props"
    [xml]$xml = Get-Content -LiteralPath $props -Raw
    $node = @($xml.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq $PackageId }) | Select-Object -First 1
    if (-not $node) { throw "Merkezi paket sürümü bulunamadı: $PackageId" }
    return [string]$node.Version
}

function Remove-BuildDirectories {
    param([Parameter(Mandatory=$true)][string]$Root)
    Get-ChildItem -LiteralPath $Root -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @("bin", "obj") } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Get-LatestDebugExe {
    param([Parameter(Mandatory=$true)][string]$BinRoot)
    if (-not (Test-Path -LiteralPath $BinRoot)) { return $null }
    return @(Get-ChildItem -LiteralPath $BinRoot -Filter "ADB.Player.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending) | Select-Object -First 1
}

function Get-LatestDebugDll {
    param([Parameter(Mandatory=$true)][string]$BinRoot)
    if (-not (Test-Path -LiteralPath $BinRoot)) { return $null }
    return @(Get-ChildItem -LiteralPath $BinRoot -Filter "ADB.Player.dll" -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.Directory.FullName "ADB.Player.runtimeconfig.json") } |
        Sort-Object LastWriteTime -Descending) | Select-Object -First 1
}

function Copy-LibMpvNativeRuntime {
    param(
        [Parameter(Mandatory=$true)][string]$Destination,
        [switch]$Required
    )

    $packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
    $version = Get-CentralPackageVersion -PackageId "Endpne.LibMPV.Windows"
    $packageRoot = Join-Path $packagesRoot "endpne.libmpv.windows\$version"
    $nativeRoot = Join-Path $packageRoot "runtimes\win-x64\native"

    $files = @()
    if (Test-Path -LiteralPath $nativeRoot) {
        $files = @(Get-ChildItem -LiteralPath $nativeRoot -File -Recurse -ErrorAction SilentlyContinue)
    }
    if ($files.Count -eq 0 -and (Test-Path -LiteralPath $packageRoot)) {
        $files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -ieq ".dll" })
    }
    if ($files.Count -eq 0) {
        if ($Required) { throw "libmpv native dosyaları NuGet önbelleğinde bulunamadı: $packageRoot" }
        return @()
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $file.Name) -Force }

    $mpv = @(Get-ChildItem -LiteralPath $Destination -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @("libmpv-2.dll", "mpv-2.dll", "libmpv.dll") }) | Select-Object -First 1
    if (-not $mpv -and $Required) { throw "Ana libmpv DLL bulunamadı: $Destination" }
    return $files
}

function Copy-W5OptionalNativeRuntime {
    param([Parameter(Mandatory=$true)][string]$Destination)
    $root = Get-ProjectRoot
    $nativeRoot = Join-Path $root "native"
    $copied = @()
    foreach ($feature in @("telegram", "torrent", "rclone")) {
        $source = Join-Path $nativeRoot $feature
        if (-not (Test-Path -LiteralPath $source)) { continue }
        $target = Join-Path (Join-Path $Destination "native") $feature
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath $source -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $relative = $_.FullName.Substring($source.Length).TrimStart('\', '/')
            $targetFile = Join-Path $target $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $targetFile -Force
            $copied += $targetFile
        }
    }
    return $copied
}

function Write-W5NativeOutputStatus {
    param([Parameter(Mandatory=$true)][string]$OutputDirectory)
    $tdlibCandidates = @(
        (Join-Path $OutputDirectory "tdjson.dll"),
        (Join-Path $OutputDirectory "native\telegram\tdjson.dll"),
        (Join-Path $OutputDirectory "runtimes\win-x64\native\tdjson.dll")
    )
    $tdlib = $tdlibCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    $rclone = Join-Path $OutputDirectory "native\rclone\rclone.exe"
    if ($tdlib) { Write-Host "TDLib: $tdlib" -ForegroundColor Green }
    else { Write-Host "TDLib: eklenmedi; Telegram özelliği çalışma anında kapalı kalacak." -ForegroundColor Yellow }
    $monoTorrent = Join-Path $OutputDirectory "MonoTorrent.Client.dll"
    if (Test-Path -LiteralPath $monoTorrent) { Write-Host "MonoTorrent akış motoru: $monoTorrent" -ForegroundColor Green }
    else { Write-Host "MonoTorrent akış motoru çıktıda bulunamadı." -ForegroundColor Yellow }
    if (Test-Path -LiteralPath $rclone) { Write-Host "rclone: $rclone" -ForegroundColor Green }
    else { Write-Host "rclone: eklenmedi; hesap tabanlı bulut özelliği kapalı kalacak. .\tool\vendor-rclone.ps1 çalıştırın." -ForegroundColor Yellow }
}

function Get-InnoCompiler {
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA "Programs"))) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        foreach ($folder in @("Inno Setup 7", "Inno Setup 6")) {
            $candidates.Add((Join-Path $base "$folder\ISCC.exe"))
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Get-Sha256 {
    param([Parameter(Mandatory=$true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}
