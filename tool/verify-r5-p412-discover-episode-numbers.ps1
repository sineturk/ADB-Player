param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$viewPath = Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\DiscoverView.xaml'
$modelPath = Join-Path $root 'src\AltyaziDB.Player.Addons\Models\CatalogModels.cs'
$vmPath = Join-Path $root 'src\AltyaziDB.Player.App\ViewModels\AddonsPanelViewModel.cs'
$servicePath = Join-Path $root 'src\AltyaziDB.Player.Addons\Services\StremioAddonClient.cs'

function Require([bool]$ok,[string]$message) {
    if (-not $ok) { throw "R5/P4.12 FAIL: $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}
foreach ($path in @($viewPath,$modelPath,$vmPath,$servicePath)) {
    Require (Test-Path -LiteralPath $path -PathType Leaf) "Required source: $([IO.Path]::GetFileName($path))"
}

[xml]$view = [IO.File]::ReadAllText($viewPath, [Text.Encoding]::UTF8)
$model = [IO.File]::ReadAllText($modelPath, [Text.Encoding]::UTF8)
$vm = [IO.File]::ReadAllText($vmPath, [Text.Encoding]::UTF8)
$service = [IO.File]::ReadAllText($servicePath, [Text.Encoding]::UTF8)

$episodes = @($view.SelectNodes("//*[local-name()='ComboBox' and @ItemsSource='{Binding Addons.Episodes}']"))
Require ($episodes.Count -eq 1) 'Discover contains exactly one episode selector'
$dropdown = $episodes[0]
Require ($dropdown.GetAttribute('DisplayMemberPath') -eq 'DisplayLabel') 'Episode selector uses number + title'
Require ($dropdown.GetAttribute('SelectedItem') -eq '{Binding Addons.SelectedEpisode, Mode=TwoWay}') 'Episode selection remains bidirectional'
Require ($model.Contains('public string NumberLabel => Season is not null && Episode is not null')) 'Episode labels prefer real season and episode metadata'
Require ($model.Contains('?$"S{Season:00}E{Episode:00}"') -or $model.Contains('? $"S{Season:00}E{Episode:00}"')) 'Episode model retains S01E01 formatting'
Require ($model.Contains('public string DisplayLabel => string.Join(') -and $model.Contains('new[] { NumberLabel, Title }')) 'Episode number and title are combined without fabricated metadata'
Require ($service.Contains('var season = GetInteger(video, "season");')) 'Season parsed from source metadata'
Require ($service.Contains('var episode = GetInteger(video, "episode");')) 'Episode number parsed from source metadata'
Require ($vm.Contains('public ObservableCollection<CatalogEpisode> Episodes { get; }')) 'Episode list type preserved'
Require ($vm.Contains('public CatalogEpisode? SelectedEpisode')) 'Episode selection and playback flow preserved'
Write-Host '[PASS] R5/P4.12 Discover episode-number presentation regression checks' -ForegroundColor Cyan
