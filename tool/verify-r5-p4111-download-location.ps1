param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$name) {
  $path = Join-Path $root $name
  if (!(Test-Path -LiteralPath $path)) { throw "R5/P4.11.1 missing $name" }
  [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}
function Require([string]$body, [string]$needle) {
  if (!$body.Contains($needle)) { throw "R5/P4.11.1 missing: $needle" }
  Write-Host "[PASS] $needle" -ForegroundColor Green
}
$settings = Read-Source 'src\AltyaziDB.Player.Core\Models\PlayerSettings.cs'
$main = Read-Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$torrent = Read-Source 'src\AltyaziDB.Player.App\ViewModels\TorrentPanelViewModel.cs'
$xaml = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml'
$view = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml.cs'
Require $settings 'public string TorrentDownloadDirectory { get; set; } = string.Empty;'
foreach ($token in @('public string TorrentDownloadDirectory =>',
 'public string TorrentStorageFreeSpace',
 'public string TorrentStorageStatus',
 'public void SetTorrentDownloadDirectory(string? selectedFolder)',
 'public void RestoreTorrentDownloadDirectory()',
 'OpenTorrentDownloadFolderCommand = new RelayCommand(',
 'Path.GetFullPath(selectedFolder.Trim())',
 'Path.IsPathFullyQualified(full)',
 'Directory.CreateDirectory(full)',
 'FileOptions.DeleteOnClose',
 'Environment.SpecialFolder.Windows',
 'IsWithinDirectory(full, applicationDirectory)',
 'OnPropertyChanged(nameof(TorrentDownloadDirectory))',
 '_settings.TorrentDownloadDirectory =',
 'Settings.TorrentStorage.Restart',
 '_ = SaveSettingsAsync()')) { Require $main $token }
foreach ($token in @('private string? _initializedDownloadDirectory;',
 'private string DownloadDirectoryForNewSession =>',
 'var directory = _initializedDownloadDirectory ?? DownloadDirectoryForNewSession;',
 'new TorrentEngineConfiguration(',
 '_initializedDownloadDirectory ??= directory;',
 'var customDirectory = !string.Equals(',
 'var delete = !customDirectory',
 'RemoveAsync(session.SessionId, delete)',
 'var directory = _initializedDownloadDirectory ?? DownloadDirectoryForNewSession;')) {
 Require $torrent $token
}
if ($torrent.Contains('Directory.CreateDirectory(_paths.TorrentCacheDirectory);')) {
 throw 'Torrent folder shortcut still points only to the default cache'
}
foreach ($token in @(
 'x:Name="TorrentDownloadDirectoryDisplay"',
 'Text="{Binding TorrentDownloadDirectory, Mode=OneWay}"',
 'Click="ChooseTorrentDownloadDirectory_Click"',
 'Click="ResetTorrentDownloadDirectory_Click"',
 'Command="{Binding OpenTorrentDownloadFolderCommand}"',
 'Text="{Binding TorrentStorageFreeSpace}"',
 'Text="{Binding TorrentStorageStatus, Converter={StaticResource UserFacingTextConverter}}"',
 'Settings.TorrentStorage.Title', 'Settings.TorrentStorage.Help')) { Require $xaml $token }
foreach ($token in @('WinForms.FolderBrowserDialog',
 'ShowNewFolderButton = true',
 'viewModel.SetTorrentDownloadDirectory(dialog.SelectedPath)',
 'viewModel.RestoreTorrentDownloadDirectory()')) { Require $view $token }
$doc = New-Object Xml.XmlDocument
$doc.Load((Join-Path $root 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml'))
foreach ($lang in @('tr-TR','en-US')) {
 $name = "src\AltyaziDB.Player.App\Localization\Strings.$lang.xaml"
 $translations = Read-Source $name
 $dict = New-Object Xml.XmlDocument
 $dict.Load((Join-Path $root $name))
 foreach ($key in @('Settings.TorrentStorage.Title',
    'Settings.TorrentStorage.Folder','Settings.TorrentStorage.Choose',
    'Settings.TorrentStorage.Open','Settings.TorrentStorage.Default',
    'Settings.TorrentStorage.Free','Settings.TorrentStorage.Restart',
    'Settings.TorrentStorage.Error','Settings.TorrentStorage.CloseKeep',
    'Settings.TorrentStorage.ConfirmDelete')) {
   Require $translations ('x:Key="' + $key + '"')
 }
}
Write-Host '[PASS] R5/P4.11.1 user download directory, privacy and safety' -ForegroundColor Cyan
