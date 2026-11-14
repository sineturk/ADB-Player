$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Source([string]$path) {
    $file = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing: $path" }
    [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
}
function Check([bool]$ok, [string]$message) {
    if (-not $ok) { throw "[P4.14.1 FAIL] $message" }
    Write-Host "[PASS] $message" -ForegroundColor Green
}

$settings = Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml'
$tr = Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
$tg = Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$code = Source 'src\AltyaziDB.Player.App\Views\Pages\SettingsView.xaml.cs'
$store = Source 'src\AltyaziDB.Player.Telegram\TelegramSevenZipStoredRangeResolver.cs'

# WPF XAML dictionaries are valid XML and must parse without duplicate keys.
$settingsXml = New-Object System.Xml.XmlDocument
$settingsXml.LoadXml($settings)
$trXml = New-Object System.Xml.XmlDocument
$trXml.LoadXml($tr)
$enXml = New-Object System.Xml.XmlDocument
$enXml.LoadXml($en)
Check ($settingsXml.DocumentElement.LocalName -eq 'UserControl') 'Settings WPF XAML parses'
Check ($trXml.DocumentElement.LocalName -eq 'ResourceDictionary') 'Turkish ResourceDictionary parses'
Check ($enXml.DocumentElement.LocalName -eq 'ResourceDictionary') 'English ResourceDictionary parses'

$ns = New-Object System.Xml.XmlNamespaceManager($settingsXml.NameTable)
$ns.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$scroller = $settingsXml.SelectSingleNode('/p:UserControl/p:ScrollViewer', $ns)
Check ($null -ne $scroller -and $null -ne $settingsXml.SelectSingleNode('/p:UserControl/p:ScrollViewer/p:StackPanel', $ns)) 'Root settings layout flows vertically'
Check (-not $settings.Contains('Grid.Row="6"')) 'Advanced expanders do not share overlapping grid rows'
Check ($settings.Contains('Settings.Sections.Account') -and
       $settings.Contains('Settings.Sections.Connections') -and
       $settings.Contains('Settings.Sections.Storage') -and
       $settings.Contains('Settings.Sections.Privacy') -and
       $settings.Contains('Settings.Sections.Advanced')) 'Five user-facing sections are present'
Check ($settings.Contains('Settings.Sections.Credentials') -and
       $settings.Contains('IsExpanded="False"')) 'Developer credentials start collapsed'

# Every on-page resource resolves for both languages, with matching placeholders.
$references = [regex]::Matches($settings, '\{DynamicResource\s+([\w.:-]+)\}') |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
foreach ($language in @(@{Name='tr-TR'; Text=$tr}, @{Name='en-US'; Text=$en})) {
    foreach ($reference in $references) {
        Check ($language.Text.Contains('x:Key="' + $reference + '"')) ('UI key ' + $language.Name + '/' + $reference)
    }
}
$cacheKeys = [regex]::Matches($tg, '"(Settings\.TelegramCache\.(?:Usage|Location|Status|Confirm)\.[^"]+)"') |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
foreach ($key in $cacheKeys) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and $en.Contains('x:Key="' + $key + '"')) ('Cache i18n: ' + $key)
}
Check ($tg.Contains('OnPropertyChanged(nameof(CacheStatus))') -and
       $tg.Contains('NotifyCacheUsageChanged()')) 'Runtime language changes refresh bound cache text'
Check ($tg.Contains('LocalizationManager.Get("Settings.TelegramCache.Confirm.Message")')) 'Clear-cache confirmation uses selected language'
Check ($settings.Contains('Telegram.CacheUsageLabel') -and
       $settings.Contains('Telegram.CacheStatus')) 'Cache controls remain bound'
Check ($settings.Contains('Command="{Binding ApiSettings.SaveNowCommand}"')) 'Save action preserved'
Check ($code.Contains('ChooseTelegramCacheDirectory_Click') -and
       $code.Contains('ResetTelegramCacheDirectory_Click')) 'Cache folder actions preserved'
Check ($store.Contains('decoded.AsSpan().SequenceEqual(direct)')) 'P4.13j Store Range validator preserved'

# Verify matching dictionary key sets and exact placeholder indices for localized cache strings.
function Dictionary([xml]$xml) {
    $table = @{}
    foreach ($item in $xml.DocumentElement.ChildNodes) {
        if ($item.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $key = $item.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        if ($table.ContainsKey($key)) { throw "Duplicate localization key: $key" }
        $table[$key] = $item.InnerText
    }
    $table
}
$trKeys = Dictionary $trXml
$enKeys = Dictionary $enXml
foreach ($key in @($trKeys.Keys | Where-Object { $_.StartsWith('Settings.Sections.') -or $_.StartsWith('Settings.TelegramCache.') })) {
    Check ($enKeys.ContainsKey($key)) ('English counterpart ' + $key)
    $trArgs = @([regex]::Matches($trKeys[$key], '\{[0-9]+(?:[^}]*)\}') | ForEach-Object { $_.Value } | Sort-Object)
    $enArgs = @([regex]::Matches($enKeys[$key], '\{[0-9]+(?:[^}]*)\}') | ForEach-Object { $_.Value } | Sort-Object)
    Check (($trArgs -join ',') -eq ($enArgs -join ',')) ('Translation placeholders ' + $key)
}
Write-Host '[PASS] R5/P4.14.1 Settings layout + TR/EN + cache safety regression' -ForegroundColor Cyan
