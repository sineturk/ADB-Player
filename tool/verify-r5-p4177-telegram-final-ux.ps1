$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$Relative) {
    $file = Join-Path $root $Relative
    if (-not (Test-Path -LiteralPath $file)) { throw "[P4.17.7] Missing $Relative" }
    return [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8)
}
function Check([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "[P4.17.7 FAIL] $Label" }
    Write-Host "[PASS] $Label" -ForegroundColor Green
}
$telegram = Read-Source 'src\AltyaziDB.Player.App\ViewModels\TelegramPanelViewModel.cs'
$main = Read-Source 'src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs'
$homeXaml = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\HomeView.xaml'
$login = Read-Source 'src\AltyaziDB.Player.App\Views\Pages\TelegramView.xaml'
$tr = Read-Source 'src\AltyaziDB.Player.App\Localization\Strings.tr-TR.xaml'
$en = Read-Source 'src\AltyaziDB.Player.App\Localization\Strings.en-US.xaml'
$project = Read-Source 'src\AltyaziDB.Player.App\AltyaziDB.Player.App.csproj'
foreach ($xaml in @($homeXaml, $login, $tr, $en)) {
    $document = New-Object System.Xml.XmlDocument
    $document.LoadXml($xaml)
}
Check ($telegram.Contains('private bool _phoneSubmissionPending;') -and
       $telegram.Contains('SetPhoneSubmissionPending(submitSavedPhone);') -and
       $telegram.Contains('AuthorizationLabel => _phoneSubmissionPending') -and
       $telegram.Contains('StatusText = AuthorizationLabel;')) 'Submitted phone uses a neutral pending state, not a second-format warning'
Check ($telegram.Contains('Authorization.Stage != TelegramAuthorizationStage.WaitingForPhoneNumber') -and
       $telegram.Contains('SetPhoneSubmissionPending(false);') -and
       $telegram.Contains('catch') -and
       $telegram.Contains('await _service.SubmitPhoneNumberAsync(PhoneNumber.Trim())')) 'TDLib remains authority for number rejection and code transition'
Check ($telegram.Contains('IsPhoneEntryVisible => !_phoneSubmissionPending') -and
       $login.Contains('R5177.Telegram.PhoneHint') -and
       $login.Contains('IsCodeEntryVisible')) 'Phone form hides while submitting and code entry still uses TDLib state'
foreach ($key in @('R5177.Telegram.PhonePending', 'R5177.Telegram.PhoneHint')) {
    Check ($tr.Contains('x:Key="' + $key + '"') -and
           $en.Contains('x:Key="' + $key + '"')) ('TR/EN auth localization: ' + $key)
}
Check ($main.Contains('UnifiedContinueWatchingPolicy.TelegramForAccount(') -and
       $main.Contains('Telegram.CurrentAccountId == verifiedTelegramAccount') -and
       $main.Contains('ContinueWatching.Add(item)') -and
       $main.Contains('visibleForArtwork') -and
       $main.Contains('ContinueWatching.ToArray()') -and
       $main.Contains('EnrichContinueWatchingArtworkAsync(visibleForArtwork)')) 'Art lookup includes only verified, displayed Telegram/local/cloud resume entries'
Check ($main.Contains('if (misses.Count == 0 || !Account.IsSignedIn)') -and
       $main.Contains('ContinueWatchingArtworkPolicy.IsTrusted(') -and
       $main.Contains('ContinueWatchingArtworkPolicy.SafeArtworkUrl(')) 'Metadata requires ADB Cloud login, trusted match, and HTTPS poster'
$logo = 'Source="/ADB.Player;component/Assets/app_icon.png"'
Check (([regex]::Matches($homeXaml, [regex]::Escape($logo)).Count -ge 2) -and
       $project.Contains('Resource Include="Assets\app_icon.png"') -and
       $homeXaml.Contains('Source="{Binding Latest.ArtworkUrl}"') -and
       $homeXaml.Contains('Source="{Binding Latest.BackdropUrl}"')) 'Bundled fallback sits underneath both Home hero/card remote artwork'
Check ($main.Contains('ContinueWatching.Clear();') -and
       $main.Contains('visibleTelegram = verifiedTelegramAccount is > 0')) 'Existing cross-account resume isolation remains intact'
Write-Host '[PASS] R5/P4.17.7 Telegram Final UX contracts verified.' -ForegroundColor Cyan
