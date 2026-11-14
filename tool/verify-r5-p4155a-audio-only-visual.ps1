$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
    $path = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $path)) { throw "Missing: $relative" }
    [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Check([bool]$condition,[string]$label) {
    if (!$condition) { throw "[P4.15.5a FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$xaml = Source 'src\AltyaziDB.Player.App\MainWindow.xaml'
$windowCode = Source 'src\AltyaziDB.Player.App\MainWindow.xaml.cs'
$model = Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$policy = Source 'src\AltyaziDB.Player.Core\Models\AudioOnlyVisualPolicy.cs'
$snapshot = Source 'src\AltyaziDB.Player.Core\Models\PlaybackSnapshot.cs'
$player = Source 'src\AltyaziDB.Player.Playback\MpvPlaybackEngine.cs'
$metadata = Source 'src\AltyaziDB.Player.App\Services\LocalAudioVisualMetadata.cs'
$packages = Source 'Directory.Packages.props'
$csproj = Source 'src\AltyaziDB.Player.App\AltyaziDB.Player.App.csproj'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'

foreach ($text in @($xaml,$tr,$en)) {
    $doc = New-Object System.Xml.XmlDocument
    $doc.LoadXml($text)
}
Check ($xaml.Contains('x:Name="AudioOnlySurface"') -and
       $xaml.Contains('Source="Assets/app_icon.png"') -and
       $xaml.Contains('Source="{Binding AudioOnlyCover}"') -and
       $xaml.Contains('Binding HasAudioOnlyCover')) 'Audio canvas has real embedded art and brand logo fallback'
Check ($xaml.Contains('x:Name="AudioMeterBar1"') -and
       $xaml.Contains('AudioOnlyQualityLabel') -and
       $xaml.Contains('AudioOnlyPlaybackStatusKey')) 'Audio canvas has compact metadata, status and ambient meter'
Check ($windowCode.Contains('VideoHost.Visibility = visible ? Visibility.Hidden : Visibility.Visible;') -and
       $windowCode.Contains('AudioOnlySurface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;') -and
       $windowCode.Contains('UpdateAudioOnlySurface();')) 'Native HwndHost is hidden for audio, restored for video and layout'
Check ($windowCode.Contains('GlassMotionEnabled') -and
       $windowCode.Contains('AnimateAudioMeter(playing)') -and
       $windowCode.Contains('BeginAnimation(ScaleTransform.ScaleYProperty, null)')) 'Optional animation stops on pause and honors reduced motion'
Check ($policy.Contains('!snapshot.HasVideo') -and
       $policy.Contains('snapshot.HasAudio') -and
       $policy.Contains('LocalAudioFilePolicy.IsAudio(snapshot.Source)') -and
       $policy.Contains('!snapshot.IsIdle')) 'Only active audio without video enables the surface'
Check ($snapshot.Contains('AudioCodecName = null') -and
       $snapshot.Contains('AudioSampleRateHz = null') -and
       $snapshot.Contains('AudioChannelCount = null') -and
       $snapshot.Contains('AudioBitrateKbps = null')) 'Existing snapshot constructor remains backward-compatible'
Check ($player.Contains('"audio-params/channel-count"') -and
       $player.Contains('"audio-params/samplerate"') -and
       $player.Contains('"audio-codec-name"') -and
       $player.Contains('SetOption("audio-display", "no")')) 'Decoder audio properties are used; MPV embedded album art does not claim a video track'
Check ($packages.Contains('TagLibSharp2" Version="0.6.0"') -and
       $csproj.Contains('PackageReference Include="TagLibSharp2"') -and
       $metadata.Contains('MediaFile.Read(source)')) 'Local album metadata uses a pinned managed parser'
Check ($metadata.Contains('MaxCoverBytes = 4 * 1024 * 1024') -and
       $metadata.Contains('LocalAudioFilePolicy.IsAudio(source)') -and
       $metadata.Contains('File.Exists(source)') -and
       !$metadata.Contains('HttpClient') -and
       !$metadata.Contains('WebRequest')) 'No remote album fetch or unbounded embedded image'
Check ($model.Contains('LoadAudioVisualMetadataAsync(') -and
       $model.Contains('Task.Run(') -and
       $model.Contains('DecodePixelWidth = 440') -and
       $model.Contains('generation != _audioVisualGeneration') -and
       $model.Contains('AudioOnlyVisualPolicy.ShouldShow(')) 'Cover parsing runs async, decodes bounded, ignores stale track completions'
foreach($name in @('Title','Playing','Paused','Buffering','Mono','Stereo')) {
    $key = 'Player.AudioOnly.' + $name
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR/EN audio-only resource: ' + $name)
}
Check ($xaml.Contains('x:Name="VideoHost"') -and
       $xaml.Contains('x:Name="MediaCenterDashboard"') -and
       $xaml.Contains('x:Name="AudioOnlySurface"')) 'Original video, playback deck and media center controls are retained'
Write-Host '[PASS] P4.15.5a audio-only cinematic surface offline contracts; not a live Windows playback test' -ForegroundColor Cyan
