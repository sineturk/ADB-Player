param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Read([string] $path) {
    $full=Join-Path $root $path
    if (!(Test-Path -LiteralPath $full)) {throw "Missing: $path"}
    return [IO.File]::ReadAllText($full)
}
function Check([string]$content,[string]$needle) {
    if (!$content.Contains($needle)) {throw "R5/P3.3 missing: $needle"}
    Write-Host "[PASS] $needle" -ForegroundColor Green
}
$xamlPath='src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
$xaml=Read $xamlPath
$view=Read 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml.cs'
$model=Read 'src\AltyaziDB.Player.App\ViewModels\SourcePanelViewModel.cs'
$rclone=Read 'src\AltyaziDB.Player.Cloud\RcloneCloudService.cs'
$langTr=Read 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$langEn=Read 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
foreach($path in @($xamlPath,'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml',
   'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml')) {
  $doc=New-Object System.Xml.XmlDocument
  $doc.Load((Join-Path $root $path))
  Write-Host "[PASS] XML $path" -ForegroundColor Green
}
Check $xaml 'x:Name="SourcesSavedTabs"'
Check $xaml 'x:Name="SourcesOAuthClientId"'
Check $xaml 'x:Name="SourcesOAuthClientSecret"'
Check $xaml 'x:Name="SourcesProviderOverlay"'
Check $xaml 'ItemsSource="{Binding FilteredCloudAccounts, ElementName=SourcesRoot}"'
Check $xaml 'ItemsSource="{Binding FilteredSavedLinks, ElementName=SourcesRoot}"'
Check $xaml 'ItemsSource="{Binding FilteredWebDavProfiles, ElementName=SourcesRoot}"'
Check $xaml 'Click="CloudDisconnect_Click"'
Check $xaml 'Click="LinkRemove_Click"'
Check $xaml 'Click="WebDavRemove_Click"'
Check $view 'SourcesOAuthClientId.Text, SourcesOAuthClientSecret.Password'
Check $view 'SourcesOAuthClientSecret.Clear()'
Check $model 'cloud:{prefix}-client-id'
Check $model 'RestoreCredentialsAsync'
Check $model 'cloud:remote:{newAccount.RemoteName}:client-id'
Check $model 'cloud:{prefix}-client-secret'
Check $model 'HasCloudConnectError = true'
Check $model 'DiagnoseOAuthFailure(exception)'
Check $model 'diagnosis.Code'
Check $model 'public async Task<bool> ConnectCloudProviderAsync'
Check $rclone '"lsjson", $"{remoteName}:"'
Check $rclone 'BuildProviderEnvironmentAsync(account.Provider, cancellationToken, account.RemoteName)'
Check $rclone 'PersistRuntimeConfigAsync(cancellationToken)'
Check $rclone 'RCLONE_DROPBOX_CLIENT_SECRET'
Check $rclone 'RCLONE_PCLOUD_CLIENT_SECRET'
Check $rclone 'cloud:remote:{remoteName}:client-secret'
if($model.Contains('ShowError($"Bulut hesabı bağlanamadı')) {
  throw "OAuth errors must not generate a second modal containing raw stderr"
}
foreach($loc in @($langTr,$langEn)) {
  foreach($key in @('R5.Sources.SavedSourcesTitle','R5.Sources.AccountsTab',
    'R5.Sources.LinksTab','R5.Sources.AdvancedOAuth','R5.Sources.OAuthClientId',
    'R5.Sources.OAuthClientSecret')) {Check $loc ('x:Key="' + $key + '"')}
}
Write-Host "[PASS] R5/P3.3 OAuth credentials, safe diagnostics and source UX guards" -ForegroundColor Cyan
