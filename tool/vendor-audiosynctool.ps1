param(
    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    . "$PSScriptRoot\common.ps1"
    $ProjectRoot = Get-ProjectRoot
}

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$nativeRoot = Join-Path $root "native\audiosynctool"
$pythonDir = Join-Path $nativeRoot "python"
$sourceDir = Join-Path $nativeRoot "source"
$workerSource = Join-Path $root "tool\audiosynctool\player_worker.py"
$workerTarget = Join-Path $nativeRoot "player_worker.py"
$readyFile = Join-Path $nativeRoot "READY.json"

$pythonVersion = "3.12.10"
$pythonUrl = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip"
$pythonMd5 = "FE8EF205F2E9C3BA44D0CF9954E1ABD3"
$numpyVersion = "2.2.6"
$numpyFile = "numpy-2.2.6-cp312-cp312-win_amd64.whl"
$numpySha256 = "C1F9540BE57940698ED329904DB803CF7A402F3FC200BFE599334C9BD84A40B2"
$scipyVersion = "1.15.3"
$scipyFile = "scipy-1.15.3-cp312-cp312-win_amd64.whl"
$scipySha256 = "52092BC0472CFD17DF49FF17E70624345EFECE4E1A12B23783A1AC59A1B728ED"
$audioSyncCommit = "b90fba80b182dae14fc72d5af02d7504794a515c"
$workerVersion = "3.2.0"
$audioSyncSourceUrl = "https://github.com/blast1see/AudioSyncTool/archive/$audioSyncCommit.zip"

function Invoke-DownloadFile {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile
    )
    $parent = Split-Path -Parent $OutFile
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
    if (-not (Test-Path -LiteralPath $OutFile -PathType Leaf)) {
        throw "İndirme başarısız: $Uri"
    }
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet("SHA256", "MD5")][string]$Algorithm,
        [Parameter(Mandatory = $true)][string]$Expected
    )
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToUpperInvariant()
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "$Algorithm doğrulaması başarısız: $Path`nBeklenen: $Expected`nBulunan: $actual"
    }
}

function Install-Wheel {
    param(
        [Parameter(Mandatory = $true)][string]$Package,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$TempRoot,
        [Parameter(Mandatory = $true)][string]$SitePackages
    )

    Write-Host "$Package $Version hazırlanıyor..." -ForegroundColor Cyan
    $metadata = Invoke-RestMethod -Uri "https://pypi.org/pypi/$Package/$Version/json"
    $asset = $metadata.urls | Where-Object { $_.filename -eq $FileName } | Select-Object -First 1
    if (-not $asset) {
        throw "PyPI üzerinde beklenen wheel bulunamadı: $FileName"
    }
    if ($asset.digests.sha256.ToUpperInvariant() -ne $ExpectedSha256.ToUpperInvariant()) {
        throw "PyPI metadata SHA-256 beklenen değerden farklı: $FileName"
    }

    $wheel = Join-Path $TempRoot $FileName
    Invoke-DownloadFile -Uri $asset.url -OutFile $wheel
    Assert-FileHash -Path $wheel -Algorithm SHA256 -Expected $ExpectedSha256

    $wheelZip = "$wheel.zip"
    Copy-Item -LiteralPath $wheel -Destination $wheelZip -Force
    $extract = Join-Path $TempRoot ("wheel-" + $Package)
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -LiteralPath $wheelZip -DestinationPath $extract -Force
    Get-ChildItem -LiteralPath $extract -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $SitePackages -Recurse -Force
    }
}

function Invoke-AudioSyncWorkerSelfTest {
    param(
        [Parameter(Mandatory = $true)][string]$PythonPath,
        [Parameter(Mandatory = $true)][string]$WorkerPath,
        [Parameter(Mandatory = $true)][string]$SourcePath
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $PythonPath
    $psi.Arguments = '"' + $WorkerPath + '" --self-test'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $root
    $psi.EnvironmentVariables["PYTHONPATH"] = $SourcePath
    $psi.EnvironmentVariables["PYTHONUTF8"] = "1"
    $psi.EnvironmentVariables["PYTHONNOUSERSITE"] = "1"

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    try {
        if (-not $process.Start()) {
            throw "AudioSync worker self-test process baslatilamadi."
        }

        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()

        $parsed = $null
        $jsonText = $null
        $startIndex = $stdout.IndexOf("{")
        $endIndex = $stdout.LastIndexOf("}")

        if ($startIndex -ge 0 -and $endIndex -ge $startIndex) {
            $jsonText = $stdout.Substring($startIndex, $endIndex - $startIndex + 1)
            try {
                $parsed = $jsonText | ConvertFrom-Json
            }
            catch {
                $parsed = $null
            }
        }

        return [pscustomobject]@{
            ExitCode = [int]$process.ExitCode
            StdOut = [string]$stdout
            StdErr = [string]$stderr
            JsonText = [string]$jsonText
            Json = $parsed
        }
    }
    finally {
        $process.Dispose()
    }
}

function Test-AudioSyncRuntime {
    if (-not (Test-Path -LiteralPath (Join-Path $pythonDir "python.exe") -PathType Leaf)) { return $false }
    if (-not (Test-Path -LiteralPath $workerTarget -PathType Leaf)) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir "audio_sync\core\analyzer.py") -PathType Leaf)) { return $false }

    try {
        $test = Invoke-AudioSyncWorkerSelfTest `
            -PythonPath (Join-Path $pythonDir "python.exe") `
            -WorkerPath $workerTarget `
            -SourcePath $sourceDir

        if ($test.ExitCode -ne 0 -or $null -eq $test.Json) {
            return $false
        }

        return (
            [bool]$test.Json.ok -and
            [string]$test.Json.worker_version -eq $workerVersion -and
            [bool]$test.Json.multimodal_timeline_sync -and
            [bool]$test.Json.progressive_adaptive_sync -and
            [bool]$test.Json.segment_verified_fallback -and
            [bool]$test.Json.normal_ast_fast_path_unchanged -and
            [bool]$test.Json.live_progressive_sync -and
            [bool]$test.Json.background_lookahead -and
            [bool]$test.Json.seek_aware_live_sync -and
            [bool]$test.Json.persistent_live_region_cache
        )
    }
    catch {
        return $false
    }
}


if (-not (Test-Path -LiteralPath $workerSource -PathType Leaf)) {
    throw "AudioSyncTool worker kaynağı bulunamadı: $workerSource"
}

# Worker köprüsü Player paketinin parçasıdır; runtime hazır olsa bile yeni worker
# sürümünü native klasöre yansıt. NumPy/SciPy ve upstream kaynak tekrar indirilmez.
if (Test-Path -LiteralPath $nativeRoot -PathType Container) {
    Copy-Item -LiteralPath $workerSource -Destination $workerTarget -Force
}

# Var olan runtime gerçekten çalışıyorsa tekrar indirme yapma.
if (Test-AudioSyncRuntime) {
    $ready = [ordered]@{
        engine = "AudioSyncTool"
        engineVersion = "2.5.0"
        commit = $audioSyncCommit
        workerVersion = $workerVersion
        python = $pythonVersion
        numpy = $numpyVersion
        scipy = $scipyVersion
        preparedAt = (Get-Date).ToString("o")
    }
    $ready | ConvertTo-Json | Set-Content -LiteralPath $readyFile -Encoding UTF8
    Write-Host ("AudioSyncTool 2.5 runtime hazır; worker {0} güncellendi: {1}" -f $workerVersion, $nativeRoot) -ForegroundColor Green
    exit 0
}

$tempRoot = Join-Path $env:TEMP ("AltyaziDB-AudioSyncTool-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    Remove-Item -LiteralPath $nativeRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $nativeRoot, $pythonDir, $sourceDir -Force | Out-Null

    Write-Host "AudioSyncTool için gömülü Python $pythonVersion indiriliyor..." -ForegroundColor Cyan
    $pythonZip = Join-Path $tempRoot "python-embed.zip"
    Invoke-DownloadFile -Uri $pythonUrl -OutFile $pythonZip
    Assert-FileHash -Path $pythonZip -Algorithm MD5 -Expected $pythonMd5
    Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonDir -Force

    $pth = Join-Path $pythonDir "python312._pth"
    if (-not (Test-Path -LiteralPath $pth -PathType Leaf)) {
        throw "Gömülü Python ._pth dosyası bulunamadı."
    }
    @(
        "python312.zip",
        ".",
        "Lib\site-packages",
        "import site"
    ) | Set-Content -LiteralPath $pth -Encoding ASCII

    $sitePackages = Join-Path $pythonDir "Lib\site-packages"
    New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null

    Install-Wheel -Package "numpy" -Version $numpyVersion -FileName $numpyFile -ExpectedSha256 $numpySha256 -TempRoot $tempRoot -SitePackages $sitePackages
    Install-Wheel -Package "scipy" -Version $scipyVersion -FileName $scipyFile -ExpectedSha256 $scipySha256 -TempRoot $tempRoot -SitePackages $sitePackages

    Write-Host "AudioSyncTool v2.5.0 analiz kaynağı indiriliyor..." -ForegroundColor Cyan
    $sourceZip = Join-Path $tempRoot "audiosynctool-source.zip"
    Invoke-DownloadFile -Uri $audioSyncSourceUrl -OutFile $sourceZip
    $sourceExtract = Join-Path $tempRoot "audiosynctool-source"
    Expand-Archive -LiteralPath $sourceZip -DestinationPath $sourceExtract -Force
    $repoRoot = Get-ChildItem -LiteralPath $sourceExtract -Directory | Where-Object { $_.Name -like "AudioSyncTool-*" } | Select-Object -First 1
    if (-not $repoRoot) {
        throw "AudioSyncTool kaynak arşivi beklenen yapıda değil."
    }
    $audioSyncPackage = Join-Path $repoRoot.FullName "audio_sync"
    if (-not (Test-Path -LiteralPath (Join-Path $audioSyncPackage "core\analyzer.py") -PathType Leaf)) {
        throw "AudioSyncTool analiz çekirdeği kaynak arşivinde bulunamadı."
    }
    Copy-Item -LiteralPath $audioSyncPackage -Destination $sourceDir -Recurse -Force
    $license = Join-Path $repoRoot.FullName "LICENSE"
    if (Test-Path -LiteralPath $license -PathType Leaf) {
        Copy-Item -LiteralPath $license -Destination (Join-Path $nativeRoot "AudioSyncTool-LICENSE.txt") -Force
    }

    Copy-Item -LiteralPath $workerSource -Destination $workerTarget -Force
    @"
AudioSyncTool v2.5.0 headless integration for ADB Player
Upstream: https://github.com/blast1see/AudioSyncTool
Pinned commit: $audioSyncCommit
Python: $pythonVersion embedded x64
NumPy: $numpyVersion ($numpyFile)
SciPy: $scipyVersion ($scipyFile)
The upstream GUI is not launched; player_worker.py calls AudioAnalyzer and the upstream SyncPipeline directly.
"@ | Set-Content -LiteralPath (Join-Path $nativeRoot "SOURCE.txt") -Encoding UTF8

    if (-not (Test-AudioSyncRuntime)) {
        throw "AudioSyncTool runtime öz testi başarısız oldu."
    }

    $ready = [ordered]@{
        engine = "AudioSyncTool"
        engineVersion = "2.5.0"
        commit = $audioSyncCommit
        workerVersion = $workerVersion
        python = $pythonVersion
        numpy = $numpyVersion
        scipy = $scipyVersion
        preparedAt = (Get-Date).ToString("o")
    }
    $ready | ConvertTo-Json | Set-Content -LiteralPath $readyFile -Encoding UTF8
    Write-Host ("AudioSyncTool 2.5 runtime hazır; worker {0}: {1}" -f $workerVersion, $nativeRoot) -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
