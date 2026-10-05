param(
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Text([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Dosya bulunamadi: $relativePath"
    }
    return [IO.File]::ReadAllText($path)
}

function Require-Text([string]$content, [string]$needle, [string]$label) {
    if (-not $content.Contains($needle)) {
        throw "V1.0.1 dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

$props = Read-Text "Directory.Build.props"
$xaml = Read-Text "src\AltyaziDB.Player.App\MainWindow.xaml"
$codeBehind = Read-Text "src\AltyaziDB.Player.App\MainWindow.xaml.cs"
$videoHostSource = Read-Text "src\AltyaziDB.Player.Playback\Controls\MpvVideoHost.cs"
$engine = Read-Text "src\AltyaziDB.Player.Playback\MpvPlaybackEngine.cs"
$videoSettings = Read-Text "src\AltyaziDB.Player.Core\Models\VideoProcessingSettings.cs"
$viewModel = Read-Text "src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs"
$portable = Read-Text "tool\publish-portable.ps1"

Require-Text $props "<Version>1.0.1.0</Version>" "Surum 1.0.1.0"
Require-Text $xaml 'x:Name="PlayerContentGrid"' "Fullscreen parent grid adlandirilmis"
Require-Text $xaml 'x:Name="FullscreenControlsPopup"' "Fullscreen Popup transport mevcut"
Require-Text $xaml 'PreviewMouseMove="Window_OnPreviewMouseMove"' "WPF pointer activity hook mevcut"
Require-Text $codeBehind 'PlayerContentGrid.Margin = new Thickness(0);' "Fullscreen sayfa boslugu kaldiriliyor"
Require-Text $codeBehind 'VideoBorder.BorderThickness = new Thickness(0);' "Mavi viewport cercevesi fullscreen'de kaldiriliyor"
Require-Text $codeBehind 'VideoHost.PointerActivity += VideoHost_OnPointerActivity;' "Native video pointer hook bagli"
Require-Text $codeBehind 'Interval = TimeSpan.FromSeconds(2.8)' "Fullscreen kontrol auto-hide zamanlayicisi mevcut"
Require-Text $videoHostSource 'public event EventHandler? PointerActivity;' "HwndHost pointer event mevcut"
Require-Text $videoHostSource 'private const int WmMouseMove = 0x0200;' "Native WM_MOUSEMOVE izleniyor"
Require-Text $videoSettings 'string TargetColorspaceHint = "auto"' "HDR colorspace hint auto/yes/no modeli"
Require-Text $videoSettings 'string D3D11OutputColorSpace = "auto"' "D3D11 colorspace modeli"
Require-Text $videoSettings 'string D3D11OutputFormat = "auto"' "D3D11 output format modeli"
Require-Text $viewModel 'TargetColorspaceHint = "yes"' "Elle HDR/SDR modunda colorspace hint zorlanabiliyor"
Require-Text $viewModel 'TargetTrc = "pq"' "HDR modu PQ transfer hedefliyor"
Require-Text $viewModel 'D3D11OutputColorSpace = "auto"' "D3D11 colorspace Windows/mpv tarafinda otomatik uzlasiliyor"
Require-Text $viewModel 'D3D11OutputFormat = "auto"' "D3D11 output format mpv/libplacebo tarafinda otomatik uzlasiliyor"
if ($viewModel.Contains('D3D11OutputColorSpace = "pq"') -or $viewModel.Contains('D3D11OutputFormat = "rgb10_a2"')) {
    throw "V1.0.1 HDR profili D3D11 swap-chain'i zorlamamali."
}
Write-Host "[OK] HDR profili D3D11 swap-chain'i zorlamiyor" -ForegroundColor Green
Require-Text $engine 'SetPropertyString("d3d11-output-csp", outputCsp);' "libmpv D3D11 colorspace uygulanir"
Require-Text $engine 'SetPropertyString("target-colorspace-hint", colorspaceHint);' "libmpv target-colorspace-hint dogru uygulanir"
Require-Text $portable 'RELEASE_NOTES_v1.0.1.md' "Portable paket V1.0.1 release notes tasiyor"

if ($viewModel.Contains('TargetColorspaceHint = true')) {
    throw "Eski boolean HDR colorspace hint kalintisi bulundu."
}
Write-Host "[OK] Eski boolean HDR colorspace hint kalintisi yok" -ForegroundColor Green

try {
    [xml]$null = $xaml
    Write-Host "[OK] MainWindow.xaml XML olarak iyi bicimli" -ForegroundColor Green
}
catch {
    throw "MainWindow.xaml XML parse hatasi: $($_.Exception.Message)"
}

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
    & dotnet build $solution --configuration Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build basarisiz oldu: exit $LASTEXITCODE"
    }
    Write-Host "[OK] Release x64 build" -ForegroundColor Green
}

Write-Host "ADB Player V1.0.1 hotfix statik dogrulamasi tamamlandi." -ForegroundColor Cyan
