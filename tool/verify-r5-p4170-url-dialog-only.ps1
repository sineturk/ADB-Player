$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$path) {
    $full = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing file: $path" }
    return [IO.File]::ReadAllText($full)
}
function Require([bool]$ok, [string]$label) {
    if (-not $ok) { throw "[P4.17.0 URL FAIL] $label" }
    Write-Host "[PASS] $label" -ForegroundColor Green
}
$xaml = Read-Source 'src/AltyaziDB.Player.App/Views/OpenUrlDialog.xaml'
$code = Read-Source 'src/AltyaziDB.Player.App/Views/OpenUrlDialog.xaml.cs'
[xml]$null = $xaml
Require ($xaml.Contains('TextWrapping="Wrap"') -and $xaml.Contains('Height="88" MinHeight="88"')) 'Long URL is displayed on several visible lines'
Require ($xaml.Contains('VerticalScrollBarVisibility="Auto"') -and $xaml.Contains('HorizontalScrollBarVisibility="Disabled"')) 'Long URL remains scrollable without horizontal cropping'
Require ($xaml.Contains('Foreground="{StaticResource TextBrush}"')) 'URL uses readable foreground color'
Require ($xaml.Contains('AcceptsReturn="False"')) 'Soft wrap never inserts newlines into address'
Require ($code.Contains('UrlTextBox.Text = text;') -and $code.Contains('UrlTextBox.Select(0, 0);') -and -not $code.Contains('UrlTextBox.SelectAll();')) 'Clipboard insertion does not leave text selected'
Require ($code.Contains('UrlTextBox.ScrollToHome();')) 'First characters of pasted URL are visible'
Require ($code.Contains('var candidate = UrlTextBox.Text.Trim();') -and $code.Contains('Url = candidate;')) 'Existing URL is passed through without truncation'
Require ($code.Contains('uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps')) 'Only HTTPS and HTTP inputs are accepted, as before'
Write-Host '[PASS] P4.17.0 URL dialog only' -ForegroundColor Green
