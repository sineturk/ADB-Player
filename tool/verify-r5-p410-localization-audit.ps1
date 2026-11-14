param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Source([string]$relative) {
  $p = Join-Path $root $relative
  if (!(Test-Path -LiteralPath $p)) { throw "Missing R5/P4.10 file: $relative" }
  return [IO.File]::ReadAllText($p)
}
function Ensure([string]$value, [string]$part) {
  if (!$value.Contains($part)) { throw "R5/P4.10 regression: $part" }
  Write-Host "[PASS] $part" -ForegroundColor Green
}
$trPath = 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$enPath = 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
$tr = New-Object Xml.XmlDocument
$en = New-Object Xml.XmlDocument
$tr.Load((Join-Path $root $trPath))
$en.Load((Join-Path $root $enPath))
$xns = 'http://schemas.microsoft.com/winfx/2006/xaml'
function Keys([Xml.XmlDocument]$doc) {
  return @($doc.SelectNodes("//*[@*[local-name()='Key' and namespace-uri()='$xns']]") |
    ForEach-Object { $_.GetAttribute('Key', $xns) } |
    Where-Object { $_ })
}
$trKeys = @(Keys $tr)
$enKeys = @(Keys $en)
$trDuplicates = @($trKeys | Group-Object | Where-Object Count -gt 1)
$enDuplicates = @($enKeys | Group-Object | Where-Object Count -gt 1)
if ($trDuplicates.Count -gt 0 -or $enDuplicates.Count -gt 0) { throw 'Duplicate localization keys' }
$missingTr = @($enKeys | Where-Object { $_ -notin $trKeys })
$missingEn = @($trKeys | Where-Object { $_ -notin $enKeys })
if ($missingTr.Count -gt 0 -or $missingEn.Count -gt 0) {
  throw "Missing localization keys in Turkish: $($missingTr -join ', '); English: $($missingEn -join ', ')"
}
Write-Host "[PASS] Resource parity: $($trKeys.Count) keys in each language"
foreach ($key in @('Dialog.Kind.Warning','Dialog.Kind.Info','Dialog.Kind.Confirm',
  'Dialog.Kind.Error','Dialog.Button.Yes','Dialog.Button.No',
  'Status.UnknownEnglishError','R5.Sources.PCloudEuropeTitle',
  'R5.Sources.PCloudUsTitle')) {
  if ($key -notin $trKeys -or $key -notin $enKeys) {
    throw "Missing localized dialog/provider resource $key"
  }
}
$dialog = Source 'src\AltyaziDB.Player.App\Views\AppDialog.xaml.cs'
foreach ($needle in @('LocalizationManager.ToUserFacingText(message)',
 'Dialog.Kind.Warning','Dialog.Kind.Info','Dialog.Kind.Confirm',
 'Dialog.Kind.Error','Dialog.Button.Yes','Dialog.Button.No')) {
  Ensure $dialog $needle
}
if ($dialog.Contains('AppDialogKind.Error => "İşlem') -or
    $dialog.Contains('AppDialogKind.Warning => "Dikkat"') -or
    $dialog.Contains('(_isConfirm ? "Evet" : "Tamam")')) {
  throw 'Hard-coded Turkish dialog texts were reintroduced'
}
Ensure (Source 'src\AltyaziDB.Player.App\Views\AppDialog.xaml') 'ToolTip="{DynamicResource Action.Close}"'
$loc = Source 'src\AltyaziDB.Player.App\Localization\LocalizationManager.cs'
foreach ($needle in @('RefreshTextBindings(window)',
 'ResourceValueTranslations.Value.En.TryGetValue',
 'ResourceValueTranslations.Value.Tr.TryGetValue',
 'LoadResourceValueTranslations()',
 'BindingOperations.GetBindingExpression(block, TextBlock.TextProperty)?.UpdateTarget()',
 'Status.UnknownEnglishError',
 'OAuth kimlik bilgileri geçersiz veya çok uzun.',
 'Telegram güvenli ayarları yüklenemedi.',
 'Eklentiler yüklenemedi.',
 'API ayarları kaydedilemedi.',
 'Bağlantı doğrulanamadı. Önceki OAuth yapılandırması korundu.',
 'Arşivden harici ses eklendi: ')) { Ensure $loc $needle }
$sourceView = Source 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml'
foreach ($needle in @(
 'Text="{Binding CloudConnectErrorText, Converter={StaticResource UserFacingTextConverter}}"',
 'Text="{Binding OAuthBrowserStatusText, Converter={StaticResource UserFacingTextConverter}}"',
 'Text="{DynamicResource R5.Sources.PCloudEuropeTitle}"',
 'Text="{DynamicResource R5.Sources.PCloudUsTitle}"')) {
  Ensure $sourceView $needle
}
$sourceCode = Source 'src\AltyaziDB.Player.App\Views\Pages\SourcesView.xaml.cs'
foreach ($needle in @('LocalizationManager.LanguageChanged += OnSourceLanguageChanged',
 'LocalizationManager.LanguageChanged -= OnSourceLanguageChanged',
 'RefreshProviderModalLanguage()',
 'LocalizationManager.TranslateMessage(option.Label)')) { Ensure $sourceCode $needle }
foreach ($path in @(
 'src\AltyaziDB.Player.App\Views\Pages\AddonsView.xaml',
 'src\AltyaziDB.Player.App\Views\Pages\LibraryView.xaml',
 'src\AltyaziDB.Player.App\MainWindow.xaml')) {
  $xml = New-Object Xml.XmlDocument
  $xml.Load((Join-Path $root $path))
}
Ensure (Source 'src\AltyaziDB.Player.App\Views\Pages\AddonsView.xaml') 'Text="{Binding SelectedAddonStateText, Converter={StaticResource UserFacingTextConverter}}"'
Ensure (Source 'src\AltyaziDB.Player.App\Views\Pages\LibraryView.xaml') 'Text="{Binding SelectedDetailStatusText, Converter={StaticResource UserFacingTextConverter}}"'
Ensure (Source 'src\AltyaziDB.Player.App\MainWindow.xaml') 'Text="{Binding LastMessage, Converter={StaticResource UserFacingTextConverter}}"'
Write-Host '[PASS] R5/P4.10 resource parity, translated errors, modal and language-switch refresh' -ForegroundColor Cyan
