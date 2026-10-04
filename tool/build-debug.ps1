. "$PSScriptRoot\common.ps1"

$root = Get-ProjectRoot
$dotnet = Get-DotNetExecutable
$config = Get-NuGetConfigPath
$project = Join-Path $root "src\AltyaziDB.Player.App\AltyaziDB.Player.App.csproj"
$binRoot = Join-Path $root "src\AltyaziDB.Player.App\bin"
Set-Location $root

$runningPlayer = Get-Process -Name "ADB.Player" -ErrorAction SilentlyContinue
if ($runningPlayer) {
    Write-Host "Açık ADB Player kapatılıyor..." -ForegroundColor Yellow
    $runningPlayer | Stop-Process -Force
    Start-Sleep -Milliseconds 750
    if (Get-Process -Name "ADB.Player" -ErrorAction SilentlyContinue) {
        throw "ADB Player işlemi kapatılamadı; derleme dosyaları kullanımda."
    }
}

& "$PSScriptRoot\verify-environment.ps1"

# Yalnız kaynak projelerin derleme çıktılarını temizle.
# Proje kökünde genel bin/obj taraması yapmak native\ffmpeg\bin gibi
# runtime klasörlerini de sildiği için burada common.ps1 içindeki eski
# Remove-BuildDirectories çağrısı özellikle kullanılmaz.
$srcRoot = Join-Path $root "src"
if (-not (Test-Path -LiteralPath $srcRoot -PathType Container)) {
    throw "Kaynak proje klasörü bulunamadı: $srcRoot"
}
Get-ChildItem -LiteralPath $srcRoot -Directory -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("bin", "obj") } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# Runtime bileşenlerini temizlikten SONRA hazırla.
$rclone = Join-Path $root "native\rclone\rclone.exe"
if (-not (Test-Path -LiteralPath $rclone)) {
    & "$PSScriptRoot\vendor-rclone.ps1"
}
if (-not (Test-Path -LiteralPath $rclone)) {
    throw "Doğrulanmış rclone.exe eklenemedi."
}

$ffmpeg = Join-Path $root "native\ffmpeg\bin\ffmpeg.exe"
$ffprobe = Join-Path $root "native\ffmpeg\bin\ffprobe.exe"
if (-not ((Test-Path -LiteralPath $ffmpeg) -and (Test-Path -LiteralPath $ffprobe))) {
    try {
        & "$PSScriptRoot\vendor-ffmpeg.ps1" -ProjectRoot $root
    }
    catch {
        Write-Warning "FFmpeg otomatik kurulamadı. Player derlenecek; otomatik ses senkronu FFmpeg kurulana kadar kapalı kalacak. $($_.Exception.Message)"
    }
}

$audioSyncPython = Join-Path $root "native\audiosynctool\python\python.exe"
$audioSyncWorker = Join-Path $root "native\audiosynctool\player_worker.py"
$audioSyncAnalyzer = Join-Path $root "native\audiosynctool\source\audio_sync\core\analyzer.py"
try {
    # Always run the lightweight vendor check. If the runtime already exists this
    # only refreshes player_worker.py and runs the embedded self-test; NumPy/SciPy
    # and upstream AudioSyncTool are not downloaded again. This keeps the native
    # worker in lockstep with the source worker after Player updates.
    Write-Host "AudioSyncTool 2.5 / Hybrid worker doğrulanıyor..." -ForegroundColor Cyan
    & "$PSScriptRoot\vendor-audiosynctool.ps1" -ProjectRoot $root
}
catch {
    Write-Warning "AudioSyncTool runtime hazırlanamadı. Player hızlı C# yedek senkron motoruyla derlenecek. $($_.Exception.Message)"
}

$subtitleSyncRoot = Join-Path $root "native\subtitlesync"
$subtitleSyncExe = Get-ChildItem -LiteralPath $subtitleSyncRoot -Include @("ffsubsync.exe", "ffs.exe", "subsync.exe") -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $subtitleSyncExe) {
    try {
        Write-Host "ffsubsync 0.5.1 altyazı senkron motoru hazırlanıyor..." -ForegroundColor Cyan
        & "$PSScriptRoot\vendor-subtitlesync.ps1" -ProjectRoot $root
    }
    catch {
        Write-Warning "ffsubsync runtime hazırlanamadı. Player derlenecek; otomatik altyazı senkronu runtime kurulana kadar kapalı kalacak. $($_.Exception.Message)"
    }
}

& "$PSScriptRoot\generate-app-credentials.ps1"

Write-Host "NuGet paketleri geri yükleniyor..." -ForegroundColor Cyan
& $dotnet restore $project `
    --configfile $config `
    --runtime win-x64 `
    --force-evaluate
if ($LASTEXITCODE -ne 0) {
    throw "NuGet geri yüklemesi başarısız oldu."
}

Write-Host "Debug x64 uygulaması derleniyor..." -ForegroundColor Cyan
& $dotnet build $project `
    --configuration Debug `
    --runtime win-x64 `
    --no-restore `
    --self-contained false `
    -p:Platform=x64 `
    -p:UseAppHost=true
if ($LASTEXITCODE -ne 0) {
    throw "Debug derlemesi başarısız oldu."
}

$exe = Get-LatestDebugExe -BinRoot $binRoot
$dll = Get-LatestDebugDll -BinRoot $binRoot

if (-not $exe -and -not $dll) {
    throw "Derleme başarılı göründü ancak uygulama çıktısı bulunamadı: $binRoot"
}

$outputDirectory = if ($exe) { $exe.Directory.FullName } else { $dll.Directory.FullName }

Copy-LibMpvNativeRuntime -Destination $outputDirectory -Required | Out-Null
Copy-W5OptionalNativeRuntime -Destination $outputDirectory | Out-Null

# FFmpeg shared build'in tamamını açıkça taşı.
# MSBuild Content öğesi de bunu yapar; bu kopya runtime için ek güvence sağlar.
$ffmpegRuntimeSource = Join-Path $root "native\ffmpeg"
$ffmpegRuntimeOutput = Join-Path $outputDirectory "native\ffmpeg"
if (Test-Path -LiteralPath $ffmpegRuntimeSource -PathType Container) {
    New-Item -ItemType Directory -Path $ffmpegRuntimeOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $ffmpegRuntimeSource -Force -ErrorAction Stop |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $ffmpegRuntimeOutput -Recurse -Force
        }
}

# AudioSyncTool headless runtime (gömülü Python + NumPy/SciPy + upstream analiz çekirdeği).
$audioSyncRuntimeSource = Join-Path $root "native\audiosynctool"
$audioSyncRuntimeOutput = Join-Path $outputDirectory "native\audiosynctool"
if (Test-Path -LiteralPath $audioSyncRuntimeSource -PathType Container) {
    New-Item -ItemType Directory -Path $audioSyncRuntimeOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $audioSyncRuntimeSource -Force -ErrorAction Stop |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $audioSyncRuntimeOutput -Recurse -Force
        }
}

# ffsubsync standalone Windows runtime.
$subtitleSyncRuntimeSource = Join-Path $root "native\subtitlesync"
$subtitleSyncRuntimeOutput = Join-Path $outputDirectory "native\subtitlesync"
if (Test-Path -LiteralPath $subtitleSyncRuntimeSource -PathType Container) {
    New-Item -ItemType Directory -Path $subtitleSyncRuntimeOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $subtitleSyncRuntimeSource -Force -ErrorAction Stop |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $subtitleSyncRuntimeOutput -Recurse -Force
        }
}

$mpv = Get-ChildItem -LiteralPath $outputDirectory -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("libmpv-2.dll", "mpv-2.dll", "libmpv.dll") } |
    Select-Object -First 1

Write-Host "Debug derlemesi tamamlandı." -ForegroundColor Green
Write-Host "libmpv: $($mpv.FullName)" -ForegroundColor Green
Write-W5NativeOutputStatus -OutputDirectory $outputDirectory

if ($exe) {
    Write-Host "EXE: $($exe.FullName)" -ForegroundColor Green
}
if ($dll) {
    Write-Host "DLL: $($dll.FullName)" -ForegroundColor DarkGray
}

$ffmpegOutput = Join-Path $outputDirectory "native\ffmpeg\bin\ffmpeg.exe"
$ffprobeOutput = Join-Path $outputDirectory "native\ffmpeg\bin\ffprobe.exe"
if ((Test-Path -LiteralPath $ffmpegOutput) -and (Test-Path -LiteralPath $ffprobeOutput)) {
    Write-Host "FFmpeg: $ffmpegOutput" -ForegroundColor Green
    Write-Host "FFprobe: $ffprobeOutput" -ForegroundColor Green
    Write-Host "FFmpeg / otomatik ses senkronu: hazır" -ForegroundColor Green
}
else {
    Write-Host "FFmpeg / otomatik ses senkronu: bileşen bulunamadı (Player normal çalışır)" -ForegroundColor Yellow
}

$audioSyncPythonOutput = Join-Path $outputDirectory "native\audiosynctool\python\python.exe"
$audioSyncWorkerOutput = Join-Path $outputDirectory "native\audiosynctool\player_worker.py"
$audioSyncAnalyzerOutput = Join-Path $outputDirectory "native\audiosynctool\source\audio_sync\core\analyzer.py"
if ((Test-Path -LiteralPath $audioSyncPythonOutput) -and (Test-Path -LiteralPath $audioSyncWorkerOutput) -and (Test-Path -LiteralPath $audioSyncAnalyzerOutput)) {
    Write-Host "AudioSyncTool 2.5 / GCC-PHAT otomatik ses senkronu: hazır" -ForegroundColor Green
}
else {
    Write-Host "AudioSyncTool 2.5 runtime bulunamadı; hızlı C# yedek senkron motoru kullanılacak" -ForegroundColor Yellow
}
