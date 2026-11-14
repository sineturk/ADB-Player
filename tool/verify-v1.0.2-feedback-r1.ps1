param(
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Text([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) { throw "Dosya bulunamadi: $relativePath" }
    return [IO.File]::ReadAllText($path)
}

function Require-Text([string]$content, [string]$needle, [string]$label) {
    if (-not $content.Contains($needle)) {
        throw "V1.0.2 feedback R1 dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

function Reject-Text([string]$content, [string]$needle, [string]$label) {
    if ($content.Contains($needle)) {
        throw "V1.0.2 feedback R1 dogrulamasi basarisiz: $label"
    }
    Write-Host "[OK] $label" -ForegroundColor Green
}

$dialog = Read-Text "src\AltyaziDB.Player.App\Views\AppDialog.xaml"
$dialogCode = Read-Text "src\AltyaziDB.Player.App\Views\AppDialog.xaml.cs"
$dialogService = Read-Text "src\AltyaziDB.Player.App\Services\WpfDialogService.cs"
$dialogContract = Read-Text "src\AltyaziDB.Player.App\Services\IUserDialogService.cs"
$openUrl = Read-Text "src\AltyaziDB.Player.App\Views\OpenUrlDialog.xaml.cs"
$mainWindow = Read-Text "src\AltyaziDB.Player.App\MainWindow.xaml"
$mainWindowCode = Read-Text "src\AltyaziDB.Player.App\MainWindow.xaml.cs"
$mainVm = Read-Text "src\AltyaziDB.Player.App\ViewModels\MainViewModel.cs"

Require-Text $dialog 'WindowStyle="None"' "ADB Player ozel dialog chrome kullaniliyor"
Require-Text $dialog 'ADB Player' "ADB Player dialog markasi mevcut"
Require-Text $dialogCode 'AppDialogKind.Error' "ADB Player hata dialog modu mevcut"
Require-Text $dialogCode 'AppDialogKind.Confirm' "ADB Player onay dialog modu mevcut"
Require-Text $dialogService 'AppDialog.ShowError' "Hata servisi ADB Player dialoguna yonlendiriliyor"
Require-Text $dialogService 'AppDialog.Confirm' "Onay servisi ADB Player dialoguna yonlendiriliyor"
Reject-Text $dialogService 'System.Windows.MessageBox.Show' "WpfDialogService Windows MessageBox kullanmiyor"
Reject-Text $openUrl 'System.Windows.MessageBox.Show' "URL uyari akisi Windows MessageBox kullanmiyor"
Require-Text $dialogContract 'title = "ADB Player"' "Dialog varsayilan basligi ADB Player"

Require-Text $mainWindowCode 'Loaded += Window_OnLoaded;' "Baslangic pencere geometri dogrulamasi bagli"
Require-Text $mainWindowCode 'screen.WorkingArea' "Aktif monitor calisma alani kullaniliyor"
Require-Text $mainWindowCode 'WindowState = WindowState.Maximized;' "Kucuk ekranlarda guvenli maximize fallback mevcut"

Require-Text $mainWindow 'PrimarySubtitleDelayDecreaseCommand' "Manuel altyazi geri alma hizli kontrolu gorunur"
Require-Text $mainWindow 'PrimarySubtitleDelayIncreaseCommand' "Manuel altyazi ileri alma hizli kontrolu gorunur"
Require-Text $mainWindow 'PrimarySubtitleDelayResetCommand' "Manuel altyazi sifirlama hizli kontrolu gorunur"
Require-Text $mainVm 'ResetManualSubtitleDelayForNewMedia();' "Yeni medyada manuel altyazi gecikmesi sifirlaniyor"
Require-Text $mainVm 'private void ResetManualSubtitleDelayForNewMedia()' "Medya bazli manuel altyazi reseti mevcut"
Require-Text $mainWindow 'x:Name="SyncControlsToggle"' "Oynaticida tek kompakt senkron paneli mevcut"
Require-Text $mainWindow 'Player.Sync.SubtitleShortcut' "Altyazi senkron kisayol ipucu gorunuyor"
Require-Text $mainWindow 'Player.Sync.AudioShortcut' "Ses senkron kisayol ipucu gorunuyor"
Require-Text $mainWindow 'ShortcutSubtitleDelayDecrease' "Altyazi manuel senkron kisayollari ayarlarda"
Require-Text $mainWindow 'ShortcutAudioDelayDecrease' "Ses manuel senkron kisayollari ayarlarda"
Require-Text $mainWindowCode 'PlayerShortcutAction.SubtitleDelayDecrease' "Altyazi manuel senkron tuslari isleniyor"
Require-Text $mainWindowCode 'PlayerShortcutAction.AudioDelayDecrease' "Ses manuel senkron tuslari isleniyor"
Require-Text $mainVm 'ShortcutSubtitleDelayDecrease' "Altyazi manuel senkron kisayol ayari ViewModelde"
Require-Text $mainVm 'ShortcutAudioDelayDecrease' "Ses manuel senkron kisayol ayari ViewModelde"
Require-Text $mainVm 'AudioDelayResetCommand' "Harici ses manuel senkron sifirlama komutu mevcut"
Require-Text $mainVm 'public bool CanAdjustSubtitleDelay => SelectedSubtitleTrack is { IsDisabled: false };' "Gomulu altyazi manuel senkron destegi mevcut"
Require-Text $mainVm 'OnPropertyChanged(nameof(CanAdjustSubtitleDelay));' "Altyazi secimi degisince manuel senkron erisimi guncelleniyor"
Require-Text $mainWindow 'Binding CanAdjustSubtitleDelay' "Gomulu altyazida senkron paneli gorunur"
Require-Text $mainWindow 'x:Name="SyncFeedbackPopup"' "Senkron geri bildirimi ayri video HWND katmaninda"
Require-Text $mainWindow 'x:Name="SyncFeedbackValue"' "Senkron geri bildirimi milisaniye degeri gosteriyor"
Require-Text $mainWindowCode 'ShowSyncFeedback("Player.Sync.Feedback.Subtitle", _viewModel.PrimarySubtitleDelaySeconds);' "Altyazi tuslari toplam ofseti gosteriyor"
Require-Text $mainWindowCode 'ShowSyncFeedback("Player.Sync.Feedback.Audio", _viewModel.ManualAudioDelaySeconds);' "Ses tuslari manuel duzeltmeyi gosteriyor"
Require-Text $mainWindowCode 'ShowSyncFeedback("Player.Sync.Feedback.NoSubtitle", null);' "Secili altyazi yoksa kullanici uyari aliyor"
Require-Text $mainWindowCode 'ShowSyncFeedback("Player.Sync.Feedback.NoAudio", null);' "Harici ses yoksa kullanici uyari aliyor"
Require-Text $mainWindow 'Header="{DynamicResource Player.Controls.Navigation}"' "Gelismis gezinme katlanabilir"
# Speed label is intentionally retained in the advanced playback settings.
# Restrict this check to the compact transport row, rather than the whole XAML file.
$transportDeckStart = $mainWindow.IndexOf('x:Name="PlaybackControlDeck"')
if ($transportDeckStart -lt 0) { throw "ADB Player transport deck bulunamadi" }
$playerSpeedRow = [regex]::Match($mainWindow.Substring($transportDeckStart), '(?s)<Grid Grid.Row="1" Margin="[^"]+">.*?</Grid>')
if (-not $playerSpeedRow.Success) {
    throw "V1.0.2 feedback R1 dogrulamasi basarisiz: Ana oynatici hiz cubugu bulunamadi"
}
Require-Text $playerSpeedRow.Value 'ItemsSource="{Binding SpeedOptions}"' "Ana oynatici hiz secimi mevcut"
Reject-Text $playerSpeedRow.Value 'Text="{DynamicResource Control.Speed}"' "Ana oynatici cubugunda gereksiz Hiz etiketi yok"
Require-Text $mainWindow 'Text="{DynamicResource Control.Speed}"' "Gelistirilmis oynatma ayarlarinda Hiz etiketi korunuyor"

# R3 P0: the sync OSD must occupy the video upper-right corner, not its center.
$syncHud = [regex]::Match($mainWindow, '(?s)<Popup x:Name="SyncFeedbackPopup".*?</Popup>')
if (-not $syncHud.Success) {
    throw "V1.0.2 R3 P0: Senkron HUD Popup bulunamadi"
}
Require-Text $syncHud.Value 'Placement="Relative"' "R3 P0: HUD merkeze degil video koordinatlarina bagli"
Reject-Text $syncHud.Value 'Placement="Center"' "R3 P0: HUD ortada degil"
Require-Text $syncHud.Value 'Width="214"' "R3 P0: HUD dar ve kompakt"
Require-Text $syncHud.Value 'FontSize="20"' "R3 P0: HUD degeri kompakt"
Require-Text $mainWindowCode 'SyncFeedbackPopup.HorizontalOffset = Math.Max(0, VideoBorder.ActualWidth - toastWidth - inset);' "R3 P0: HUD sag uca dayali"
Require-Text $mainWindowCode 'SyncFeedbackPopup.VerticalOffset = inset;' "R3 P0: HUD ustte guvenli boslukla"
Require-Text $mainWindowCode 'Interval = TimeSpan.FromSeconds(1.5)' "R3 P0: HUD kisa surede kayboluyor"
Require-Text $mainWindowCode '_syncFeedbackHideTimer.Stop();' "R3 P0: Tekrarli tus girislerinde zamanlayici yenileniyor"
Require-Text $mainWindowCode 'if (SyncFeedbackPopup.IsOpen)' "R3 P0: Pencere boyutu degisince HUD konumu korunuyor"
Require-Text $mainWindow 'SizeChanged="VideoBorder_OnSizeChanged"' "R3 P0: Sidebar degisince HUD konumu korunuyor"
Require-Text $mainWindowCode 'private void VideoBorder_OnSizeChanged' "R3 P0: Video boyutu takip ediliyor"
Require-Text $mainWindow 'ToolTip="{Binding StatusMessage, Converter={StaticResource UserFacingTextConverter}}"' "R3 P0: Kisaltlan durumun tam metni gorulebilir"
Require-Text $mainWindow 'x:Name="CompactPlaybackStatus"' "R3 P0: Kisa durum metni oynatma dugmeleri yaninda"
Require-Text $mainWindow 'Width="44" Height="44" MinWidth="44" Content="{Binding PlayPauseText}"' "R4.1: Ana oynatma dugmesi daha kompakt"
Require-Text $mainWindow '<Setter Property="Height" Value="20"/>' "R3 P0: Seek bar alani kompakt"
$navSection = [regex]::Match($mainWindow, '(?s)<Expander Style="\{StaticResource PlayerSettingsExpanderStyle\}"\s+Header="\{DynamicResource Player.Controls.Navigation\}"\s+IsExpanded="False">')
if (-not $navSection.Success) {
    throw "V1.0.2 R3 P0: Gelismis oynatma gezinme ayarlari varsayilan kapali degil"
}
Write-Host "[OK] R3 P0: Gelismis gezinme ayarlari varsayilan kapali" -ForegroundColor Green

# R3 P1: keep all keyboard timing commands while removing decorative chrome.
$secondaryTransport = [regex]::Match($mainWindow, '(?s)<Style x:Key="PlayerTransportButtonStyle".*?</Style>')
$primaryTransport = [regex]::Match($mainWindow, '(?s)<Style x:Key="PlayerPrimaryTransportButtonStyle".*?</Style>')
$timePill = [regex]::Match($mainWindow, '(?s)<Style x:Key="PlayerTimePillStyle".*?</Style>')
$seekSlider = [regex]::Match($mainWindow, '(?s)<Style x:Key="PlayerSeekSliderStyle".*?</Style>')
$syncSheet = [regex]::Match($mainWindow, '(?s)<Popup PlacementTarget="\{Binding ElementName=SyncControlsToggle\}".*?</Popup>')
foreach ($section in @($secondaryTransport, $primaryTransport, $timePill, $seekSlider, $syncSheet)) {
    if (-not $section.Success) {
        throw "V1.0.2 R3 P1: Beklenen oynatici bolumu eksik"
    }
}
Require-Text $secondaryTransport.Value '<Setter Property="Background" Value="Transparent"/>' "R3 P1: Yardimci oynatma tuslari sade"
Reject-Text $primaryTransport.Value 'OuterRing' "R3 P1: Oynat tusunun ikinci halkasi kaldirildi"
Require-Text $timePill.Value '<Setter Property="BorderBrush" Value="Transparent"/>' "R3 P1: Zaman etiketleri cercevesiz"
Require-Text $seekSlider.Value '<Border Height="2"' "R3 P1: Seek cizgisi 2 piksel"
Require-Text $seekSlider.Value 'x:Name="SeekThumb" Width="10" Height="10"' "R3 P1: Seek tutamaci ince"
Require-Text $syncSheet.Value '<Border Width="390"' "R3 P1: Senkron paneli daha kompakt"
Require-Text $syncSheet.Value 'PrimarySubtitleDelayDecreaseCommand' "R3 P1: Altyazi eksi komutu korundu"
Require-Text $syncSheet.Value 'PrimarySubtitleDelayIncreaseCommand' "R3 P1: Altyazi arti komutu korundu"
Require-Text $syncSheet.Value 'PrimarySubtitleDelayResetCommand' "R3 P1: Altyazi sifirlama korundu"
Require-Text $syncSheet.Value 'AudioDelayDecreaseCommand' "R3 P1: Harici ses eksi komutu korundu"
Require-Text $syncSheet.Value 'AudioDelayIncreaseCommand' "R3 P1: Harici ses arti komutu korundu"
Require-Text $syncSheet.Value 'AudioDelayResetCommand' "R3 P1: Harici ses sifirlama korundu"
Require-Text $syncSheet.Value 'x:Name="AudioSyncDetailsToggle"' "R3 P1: Gelismis ses tanisi korundu"
Require-Text $mainVm 'if (!HasExternalAudio)' "R3 P1: Harici ses yoksa ofset gizlenir"
Require-Text $mainVm '? "0 ms"' "R3 P1: Harici ses ofseti sifirsa 0 ms gosterilir"

# R3 P2: fullscreen fade/auto-hide and a borderless Mini Player with an
# independent native HWND overlay (needed above libmpv's HwndHost).
$fullscreenHud = [regex]::Match($mainWindow, '(?s)<Popup x:Name="FullscreenControlsPopup".*?</Popup>')
$miniHeader = [regex]::Match($mainWindow, '(?s)<Popup x:Name="MiniPlayerHeaderPopup".*?</Popup>')
$miniControls = [regex]::Match($mainWindow, '(?s)<Popup x:Name="MiniPlayerControlsPopup".*?</Popup>')
foreach ($section in @($fullscreenHud, $miniHeader, $miniControls)) {
    if (-not $section.Success) {
        throw "V1.0.2 R3 P2: Tam ekran veya Mini Popup eksik"
    }
}
Require-Text $fullscreenHud.Value 'PopupAnimation="Fade"' "R3 P2: Tam ekran kontrolleri fade ile aciliyor"
Require-Text $fullscreenHud.Value '<Border x:Name="FullscreenControlPanel"' "R3 P2: Tam ekran HUD bagli"
Require-Text $fullscreenHud.Value 'Background="{StaticResource PlayerGlassPopupBrush}"' "R3 P2: Tam ekran HUD yeni cam paletini kullaniyor"
# P4.15: shorter idle timer requested. Keep a bounded interval instead of
# hard-coding the former 2.4 second default.
$fullscreenTimeout = [regex]::Match($mainWindowCode, '(?s)_fullscreenControlsHideTimer = new DispatcherTimer.*?Interval = TimeSpan.FromSeconds\((?<seconds>[0-9.]+)\)')
if (-not $fullscreenTimeout.Success) {
    throw "R3 P2: Tam ekran zamanlayicisi eksik"
}
$fullscreenSeconds = [double]::Parse($fullscreenTimeout.Groups['seconds'].Value, [Globalization.CultureInfo]::InvariantCulture)
if ($fullscreenSeconds -lt 1.2 -or $fullscreenSeconds -gt 2.4) { throw "R3 P2: Tam ekran auto-hide suresi aralik disi" }
Write-Host "[OK] R3 P2: Tam ekran auto-hide suresi" -ForegroundColor Green
Require-Text $mainWindowCode 'FullscreenControlPanel.IsMouseOver || _fullscreenSeekDragging' "R3 P2: Kontroller kullanilirken gizlenmez"
Require-Text $miniHeader.Value 'Player.Mini.Restore' "R3 P2: Mini oynaticida gorunur geri donus"
Require-Text $miniControls.Value 'MiniPlayerSeekSlider_OnPreviewMouseLeftButtonDown' "R3 P2: Mini seek dogrudan kullanilabilir"
Require-Text $miniControls.Value 'SeekBackwardCommand' "R3 P2: Mini geri sarma korundu"
Require-Text $miniControls.Value 'PlayPauseCommand' "R3 P2: Mini oynat/duraklat korundu"
Require-Text $miniControls.Value 'SeekForwardCommand' "R3 P2: Mini ileri sarma korundu"
Require-Text $miniControls.Value 'Value="{Binding Volume, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"' "R3 P2: Mini ses duzeyi kontrolu"
Require-Text $mainWindowCode 'WindowStyle = WindowStyle.None;' "R3 P2: Mini cercevesiz pencere"
Require-Text $mainWindowCode 'ControlsRow.Height = new GridLength(0);' "R3 P2: Buyuk oynatici araci Mini modda gizli"
Require-Text $mainWindowCode 'SetCurrentValue(TopmostProperty, true);' "R3 P2: Mini her zaman ustte"
Require-Text $mainWindowCode 'SetCurrentValue(TopmostProperty, _previousTopmost);' "R3 P2: Uste tut ayari cikista geri yuklenir"
Require-Text $mainWindowCode 'MinWidth = _previousMinWidth;' "R3 P2: Normal minimum genislik geri yuklenir"
Require-Text $mainWindowCode 'MinHeight = _previousMinHeight;' "R3 P2: Normal minimum yukseklik geri yuklenir"
Require-Text $mainWindowCode 'screen.WorkingArea.Right' "R3 P2: Mini konumu monitor sinirlarinda"
Require-Text $mainWindowCode 'private void HideMiniPlayerControls()' "R3 P2: Mini Popup kapanista temizleniyor"
Require-Text $mainWindowCode 'private void MiniPlayerHeader_OnMouseLeftButtonDown' "R3 P2: Mini pencere suruklenebilir"
Require-Text $mainWindowCode 'private void MiniPlayerSeekSlider_OnLostMouseCapture' "R3 P2: Mini seek mouse capture kaybi guvenli"
Require-Text $mainWindowCode 'if (!_isMiniPlayer && (_viewModel.IsMediaCenterVisible || !_viewModel.HasMedia))' "R3 P2: Video yokken Mini moda gecilmez"
Require-Text $mainWindow 'Deactivated="Window_OnDeactivated"' "R3 P2: Odak kaybinda tam ekran davranisi bagli"
Require-Text $mainWindowCode 'private void Window_OnDeactivated' "R3 P2: Uygulama odaktan cikinca kontrol katmani duzenlenir"
Require-Text $mainWindowCode 'if (_isMiniPlayer && VideoBorder.ActualWidth < 440)' "R3 P2: Dar Mini pencerede senkron toasti caprazlanmaz"
Require-Text $mainWindowCode 'MiniPlayerHeaderPopup.HorizontalOffset = 10;' "R3 P2: Mini ust baslik senkron bildiriminden ayri"
Require-Text $mainWindow 'MouseMove="MiniPlayerControls_OnMouseMove"' "R3 P2: Mini kontrol hover olayi bagli"
Require-Text $mainWindowCode '_miniControlsHideTimer = new DispatcherTimer(DispatcherPriority.Normal)' "R3 P2: Mini auto-hide zamanlayicisi var"
$miniTimeout = [regex]::Match($mainWindowCode, '(?s)_miniControlsHideTimer = new DispatcherTimer.*?Interval = TimeSpan.FromSeconds\((?<seconds>[0-9.]+)\)')
if (-not $miniTimeout.Success) { throw "P4.15: mini idle timer eksik" }
$miniSeconds = [double]::Parse($miniTimeout.Groups['seconds'].Value, [Globalization.CultureInfo]::InvariantCulture)
if ($miniSeconds -lt 1.2 -or $miniSeconds -gt 2.6) { throw "P4.15: mini idle timer aralik disi" }
Write-Host "[OK] P4.15: mini idle hide timer" -ForegroundColor Green
Require-Text $mainWindowCode 'MiniPlayerControlPanel.IsMouseOver ||' "R3 P2: Mini alt bar hover korumasi"
Require-Text $mainWindowCode 'MiniPlayerHeaderPanel.IsMouseOver ||' "Mini hover: Ust geri donus alani hover korumasi"
Require-Text $mainWindowCode '_miniSeekDragging))' "Mini hover: Seek sirasinda iki panel korunur"
Require-Text $mainWindowCode 'MiniPlayerControlsPopup.IsOpen = false;' "R3 P2: Mini alt kontroller gizlenebilir"
Require-Text $mainWindowCode 'MiniPlayerHeaderPopup.IsOpen = true;' "Mini hover: Ust ve alt bar birlikte acilir"
Require-Text $mainWindowCode 'MiniPlayerHeaderPopup.IsOpen = false;' "Mini hover: Ust ve alt bar birlikte gizlenir"

$miniHideTick = [regex]::Match($mainWindowCode, '(?s)private void MiniControlsHideTimer_OnTick\(.*?\n    }')
if (-not $miniHideTick.Success) { throw "Mini hover: Timer fonksiyonu bulunamadi" }
Require-Text $miniHideTick.Value 'MiniPlayerControlsPopup.IsOpen = false;' "Mini hover: Timer alt barı gizler"
Require-Text $miniHideTick.Value 'MiniPlayerHeaderPopup.IsOpen = false;' "Mini hover: Timer ust seridi gizler"
Require-Text $miniHideTick.Value 'MiniPlayerHeaderPanel.IsMouseOver' "Mini hover: Geri donus butonuna erisim sirasinda gizlenmez"
Require-Text $mainWindowCode 'ShowMiniPlayerControls();' "Mini hover: Fare aktivitesinde iki HUD gorunur"

Require-Text $miniHeader.Value 'MouseMove="MiniPlayerControls_OnMouseMove"' "Mini hover: Ust bar uzerinde fare aktivitesi sureyi yeniler"
Require-Text $mainWindowCode '_miniControlsHideTimer.Tick -= MiniControlsHideTimer_OnTick;' "R3 P2: Mini timer kapanista temizlenir"
Require-Text $mainWindowCode 'MiniPlayerControls_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)' "R3 P2: Mini hover WPF mouse olay turu acik"
Require-Text $mainWindowCode 'MiniPlayerSeekSlider_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)' "R3 P2: Mini seek WPF mouse olay turu acik"
Reject-Text $mainWindowCode 'MiniPlayerControls_OnMouseMove(object sender, MouseEventArgs e)' "R3 P2: Mini hover belirsiz MouseEventArgs yok"
Reject-Text $mainWindowCode 'MiniPlayerSeekSlider_OnLostMouseCapture(object sender, MouseEventArgs e)' "R3 P2: Mini seek belirsiz MouseEventArgs yok"

# Focused Playback P0: app chrome is hidden only while media is loaded.
# Access to library/open/playlist remains on the native top-edge hover bar.
Require-Text $mainWindowCode 'e.PropertyName == nameof(MainViewModel.HasMedia)' "Focused P0: Medya durumu degisince chrome yenileniyor"
Require-Text $mainWindowCode 'var focused = !modern && _viewModel.HasMedia;' "Focused P0: Medya merkezi ve bos ekran header koruyor"
Require-Text $mainWindowCode 'HeaderRow.Height = headerHeight;' "Focused P0: Baslik yuksekligi oynatmada sifirlaniyor"
Require-Text $mainWindowCode 'LegacyHeaderBorder.Visibility = headerVisibility;' "Focused P0: Logo ve ust menu oynatmada gizli"
Require-Text $mainWindowCode 'PlayerContentGrid.Margin = margin;' "Focused P0: Video kenar bosluklari kaldiriliyor"
Require-Text $mainWindow 'x:Name="PlaybackControlDeck"' "Focused P0: Alt kontroller oynatmada korunuyor"
Require-Text $mainWindow 'x:Name="FocusedPlaybackActionsPopup"' "Focused P0: Video uzerinden erisilebilir ust kontrol var"
Require-Text $mainWindowCode 'VideoBorder.PointFromScreen' "Focused P0: Native libmpv fare konumu kullaniliyor"
Require-Text $mainWindowCode 'point.Y > 74' "Focused P0: Ust kenar disinda menu acilmiyor"
Require-Text $mainWindowCode 'FocusedPlaybackActionPanel.IsMouseOver' "Focused P0: Hover menu kullanilirken gizlenmez"
Require-Text $mainWindowCode 'private void HideFocusedPlaybackActions()' "Focused P0: Ust menu gizleme fonksiyonu mevcut"
Require-Text $mainWindow 'Command="{Binding ShowMediaCenterCommand}"' "Focused P0: Medya merkezine donus erisilebilir"
Require-Text $mainWindow 'Command="{Binding OpenFileCommand}"' "Focused P0: Dosya acma erisilebilir"
Require-Text $mainWindow 'Command="{Binding OpenUrlCommand}"' "Focused P0: Baglanti acma erisilebilir"
Require-Text $mainWindowCode 'UpdatePlaybackFocusChrome();' "Focused P0: Tam ekran/miniden donuste chrome yenileniyor"
$centerSettings = [regex]::Match($mainWindow, '(?s)<Popup x:Name="PlaybackCenterSheet".*?</Popup>')
$centerSubtitles = [regex]::Match($mainWindow, '(?s)<Popup x:Name="DualSubtitleCenterSheet".*?</Popup>')
foreach ($sheet in @($centerSettings, $centerSubtitles)) {
    if (-not $sheet.Success) { throw "Focused P0: Ayarlar / CC2 merkez penceresi eksik" }
    Require-Text $sheet.Value 'Placement="Center"' "Focused P0: Sheet merkeze yerlestiriliyor"
    Require-Text $sheet.Value 'ElementName=PlayerContentGrid' "Focused P0: Sheet video yerine pencere iceriginde ortali"
    Require-Text $sheet.Value 'CenterSheetClose_OnClick' "Focused P0: Sheet kapatma dugmesi var"
    Require-Text $sheet.Value 'CenterSheet_OnPreviewKeyDown' "Focused P0: ESC sheet icinde calisiyor"
}
Require-Text $mainWindowCode 'private void CenterSheetToggle_OnChecked' "Focused P0: Iki sheet birden acilamiyor"
Require-Text $mainWindowCode 'PlaybackSettingsSheetPanel.MaxHeight' "Focused P0: Ayarlar kucuk ekranlarda sinirlandiriliyor"
Require-Text $mainWindowCode 'DualSubtitleSheetPanel.MaxHeight' "Focused P0: CC2 kucuk ekranlarda sinirlandiriliyor"
Reject-Text $centerSettings.Value 'Placement="Top"' "Focused P0: Ayarlar sag alt popup degil"
Reject-Text $centerSubtitles.Value 'Placement="Top"' "Focused P0: CC2 sag alt popup degil"
Require-Text $mainWindowCode '_focusedActionsHideTimer.Tick -= FocusedActionsHideTimer_OnTick;' "Focused P0: Hover timer kapanista temizleniyor"
Require-Text $mainWindowCode 'FocusedPlaybackActions_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)' "Focused P0: WPF MouseEventArgs acik"
Reject-Text $mainWindowCode 'FocusedPlaybackActions_OnMouseMove(object sender, MouseEventArgs e)' "Focused P0: Belirsiz Forms/WPF MouseEventArgs yok"
Require-Text $mainWindowCode 'if (e.Key == Key.Escape' "Focused P0: ESC ile merkez panelleri kapatma etkin"


# Cinematic Dark Glass P1 — shared, low-cost translucent appearance.
# Never apply a WPF visual BlurEffect to native libmpv's video surface.
Require-Text $mainWindow 'x:Key="PlayerGlassShellBrush"' "Glass P1: Oynatici cam alt zemin paleti var"
Require-Text $mainWindow 'x:Key="PlayerGlassPopupBrush"' "Glass P1: Popup cam paleti var"
Require-Text $mainWindow 'x:Key="PlayerGlassChipBrush"' "Glass P1: Kompakt cam kapsul paleti var"
Require-Text $mainWindow 'x:Key="PlayerGlassTransportBrush"' "Glass P1: Transport cam paleti var"
Require-Text $mainWindow 'x:Key="PlayerGlassToastBrush"' "Glass P1: Senkron bildirimi cam paleti var"
Require-Text $mainWindow 'x:Key="PlayerGlassStrokeBrush"' "Glass P1: Ince cam kenar vurgu rengi var"
Require-Text $mainWindow 'x:Key="PlayerGlassTextBrush"' "Glass P1: Kontrastli metin fircasi var"
$glassDeck = [regex]::Match($mainWindow, '(?s)<Border x:Name="PlaybackControlDeck".*?</Border.Effect>')
$glassSettings = [regex]::Match($mainWindow, '(?s)<Border x:Name="PlaybackSettingsSheetPanel".*?</Border.Effect>')
$glassDual = [regex]::Match($mainWindow, '(?s)<Border x:Name="DualSubtitleSheetPanel".*?</Border.Effect>')
$glassToast = [regex]::Match($mainWindow, '(?s)<Popup x:Name="SyncFeedbackPopup".*?</Popup>')
$glassSync = [regex]::Match($mainWindow, '(?s)<!-- R3 P1: one lean timing sheet.*?</Popup>')
$glassHover = [regex]::Match($mainWindow, '(?s)<Popup x:Name="FocusedPlaybackActionsPopup".*?</Popup>')
foreach ($section in @($glassDeck, $glassSettings, $glassDual, $glassToast, $glassSync, $glassHover)) {
    if (-not $section.Success) { throw "Glass P1: Oynatici cam bolumu bulunamadi" }
}
Require-Text $glassDeck.Value 'Background="{StaticResource PlayerGlassShellBrush}"' "Glass P1: Alt kontrol barinda cam zemini var"
Require-Text $glassDeck.Value 'DropShadowEffect' "Glass P1: Alt kontrol barinda hafif golge var"
Require-Text $mainWindow '<Setter Property="Background" Value="{StaticResource PlayerGlassTransportBrush}"/>' "Glass P1: Ana oynatma kontrol grubu cam"
Require-Text $mainWindow 'Background="{StaticResource PlayerGlassChipBrush}"' "Glass P1: Kontrol kapsulleri cam"
Require-Text $glassSettings.Value 'PlayerGlassPopupBrush' "Glass P1: Ayarlar merkezi cam tasarim"
Require-Text $glassDual.Value 'PlayerGlassPopupBrush' "Glass P1: CC2 merkezi cam tasarim"
Require-Text $glassSync.Value 'PlayerGlassPopupBrush' "Glass P1: Senkron menu cam tasarim"
Require-Text $glassToast.Value 'PlayerGlassToastBrush' "Glass P1: Sag ust senkron toasti cam tasarim"
Require-Text $glassHover.Value 'PlayerGlassPopupBrush' "Glass P1: Video ustu hover menu cam tasarim"
Reject-Text $glassDeck.Value 'BlurEffect' "Glass P1: Buyuk oynatma paneline pahali blur uygulanmiyor"
Reject-Text $mainWindow 'BlurEffect Radius=' "Glass P1: Native video katmani blur ile bozunmuyor"

# Immersive Glass P2 — use one glass identity for fullscreen + Mini Player.
# UI transitions are deliberately scoped to WPF overlay elements.
Require-Text $fullscreenHud.Value 'Background="{StaticResource PlayerGlassPopupBrush}"' "Glass P2: Tam ekran cam panel"
Require-Text $miniHeader.Value 'Background="{StaticResource PlayerGlassPopupBrush}"' "Glass P2: Mini ust geri donus cubugu cam"
Require-Text $miniControls.Value 'Background="{StaticResource PlayerGlassPopupBrush}"' "Glass P2: Mini video kontrol katmani cam"
Require-Text $mainWindowCode 'private void GlassPopup_OnOpened' "Glass P2: Popup acilis animasyonu var"
Require-Text $mainWindowCode 'private static bool GlassMotionEnabled => SystemParameters.MenuAnimation;' "Glass P2: Sistem animasyon tercihi korunuyor"
Require-Text $mainWindowCode 'popup.Child is not FrameworkElement surface' "Glass P2: Yalniz popup WPF icerigi canlandiriliyor"
Require-Text $mainWindowCode 'new DoubleAnimation(0.25, 1, duration)' "Glass P2: Cam overlay kisa fade"
Require-Text $mainWindowCode 'new DoubleAnimation(0.985, 1, duration)' "Glass P2: Merkez ayar penceresi nazik olcek animasyonu"
Require-Text $mainWindowCode 'AnimateShellEntrance(PlaybackControlDeck);' "Glass P2: Medya merkezinden oynaticiya gecis"
Require-Text $mainWindowCode 'AnimateShellEntrance(ModernMediaCenter);' "Glass P2: Oynaticidan medya merkezine gecis"
Require-Text $mainWindowCode 'target.BeginAnimation(OpacityProperty' "Glass P2: Yalniz shell opakligi canlandiriliyor"
Reject-Text $mainWindowCode 'VideoHost.BeginAnimation' "Glass P2: Native mpv video animasyonu yok"
Reject-Text $mainWindowCode 'VideoBorder.BeginAnimation' "Glass P2: Native mpv kapsayicisi animasyonu yok"
foreach ($popupName in @('FocusedPlaybackActionsPopup', 'SyncFeedbackPopup', 'FullscreenControlsPopup', 'MiniPlayerHeaderPopup', 'MiniPlayerControlsPopup', 'DualSubtitleCenterSheet', 'PlaybackCenterSheet')) {
    $popupSection = [regex]::Match($mainWindow, '(?s)<Popup x:Name="' + [regex]::Escape($popupName) + '".*?</Popup>')
    if (-not $popupSection.Success) { throw "Glass P2: Popup eksik: $popupName" }
    Require-Text $popupSection.Value 'PopupAnimation="Fade"' "Glass P2: $popupName yumusak aciliyor"
    Require-Text $popupSection.Value 'Opened="GlassPopup_OnOpened"' "Glass P2: $popupName acilis gecisi bagli"
}
Require-Text $glassSync.Value 'Opened="GlassPopup_OnOpened"' "Glass P2: Senkron paneline animasyon baglandi"


# R4 — Overlay Playback Shell: preserve the single set of player commands
# while moving its visual parent above the libmpv native HWND.
$overlayPopup = [regex]::Match($mainWindow, '(?s)<Popup x:Name="OverlayPlaybackControlsPopup".*?/>')
$overlayDeckIndex = $mainWindow.IndexOf('x:Name="PlaybackControlDeck"')
$overlayPopupIndex = $mainWindow.IndexOf('x:Name="OverlayPlaybackControlsPopup"')
if (-not $overlayPopup.Success -or $overlayDeckIndex -lt 0 -or $overlayPopupIndex -le $overlayDeckIndex) {
    throw "R4: Video ustu overlay veya ana playback deck bulunamadi"
}
Require-Text $mainWindow 'x:Name="RootLayoutGrid"' "R4: Tek playback deck ana parent Grid bagli"
Require-Text $overlayPopup.Value 'PlacementTarget="{Binding ElementName=VideoBorder}"' "R4: Kontroller videoya bagli"
Require-Text $overlayPopup.Value 'Placement="Relative"' "R4: Video icinde goreli overlay konumu"
Require-Text $overlayPopup.Value 'AllowsTransparency="True"' "R4: Native Popup cam kompozisyonu"
Require-Text $overlayPopup.Value 'Opened="OverlayPlaybackControls_OnOpened"' "R4: Cam overlay acilis gecisi"
Require-Text $mainWindowCode 'RootLayoutGrid.Children.Remove(PlaybackControlDeck);' "R4: Tek kontrol cubugu Grid disina tasiniyor"
Require-Text $mainWindowCode 'OverlayPlaybackControlsPopup.Child = PlaybackControlDeck;' "R4: Ayni kontroller native Popup icine tasiniyor"
Require-Text $mainWindowCode 'OverlayPlaybackControlsPopup.Child = null;' "R4: Overlay kapatilinca parent geri baglaniyor"
Require-Text $mainWindowCode 'RootLayoutGrid.Children.Add(PlaybackControlDeck);' "R4: Standart dock geri yukleniyor"
Require-Text $mainWindowCode 'ControlsRow.Height = new GridLength(0);' "R4: Video oynarken alt bar yer ayirmiyor"
Require-Text $mainWindowCode 'VideoBorder.ActualWidth >= 940' "R4: Dar alanlarda dock fallback korunuyor"
Require-Text $mainWindowCode 'private void PositionOverlayPlaybackControls()' "R4: Video boyutuna gore bottom overlay konumlandiriliyor"
Require-Text $mainWindowCode 'videoHeight - deckHeight - 14' "R4: Kontroller video altinda konumlanir"
$overlayTimeout = [regex]::Match($mainWindowCode, '(?s)_overlayControlsHideTimer = new DispatcherTimer.*?Interval = TimeSpan.FromSeconds\((?<seconds>[0-9.]+)\)')
if (-not $overlayTimeout.Success) { throw "P4.15: overlay idle timer eksik" }
$overlaySeconds = [double]::Parse($overlayTimeout.Groups['seconds'].Value, [Globalization.CultureInfo]::InvariantCulture)
if ($overlaySeconds -lt 1.2 -or $overlaySeconds -gt 2.8) { throw "P4.15: overlay idle timer aralik disi" }
Write-Host "[OK] P4.15: overlay idle hide timer" -ForegroundColor Green
Require-Text $mainWindowCode 'PlaybackControlDeck.IsMouseOver' "R4: Fare kontrol ustundeyken gizleme yok"
Require-Text $mainWindowCode 'PlaybackSpeedComboBox.IsDropDownOpen' "R4: Hiz dropdown acikken gizleme yok"
Require-Text $mainWindowCode 'PlaybackAudioTrackComboBox.IsDropDownOpen' "R4: Ses dropdown acikken gizleme yok"
Require-Text $mainWindowCode 'PlaybackSubtitleTrackComboBox.IsDropDownOpen' "R4: Altyazi dropdown acikken gizleme yok"
Reject-Text $mainWindowCode 'PlaybackControlDeck.IsKeyboardFocusWithin' "R4: Tiklanmis buton odagi kontrolleri sonsuza dek acik tutmuyor"
Require-Text $mainWindowCode 'SeekPreviewPopup.IsOpen = false;' "R4: Seek onizleme reparent oncesinde kapanir"
Require-Text $mainWindowCode 'private void OverlayPlaybackControls_OnOpened' "R4: Overlay acilis animasyonu aktif"
Require-Text $mainWindow 'x:Name="OverlayControlsPinToggle"' "R4: Cam kontrolleri ekranda tutma secenegi var"
Require-Text $mainWindowCode 'OverlayControlsPinToggle.IsChecked == true' "R4: Sabitlenmis kontroller gizlenmez"
Require-Text $mainWindowCode 'key == Key.F6' "R4: Klavyeyle overlay acma destegi"
Require-Text $mainWindow 'x:Key="PlayerOverlayGlassBrush"' "R4: Video uzerindeki bar daha seffaf cam paletinde"
Require-Text $mainWindowCode '"PlayerOverlayGlassBrush" : "PlayerGlassShellBrush"' "R4: Overlay ve dock ayrik cam zeminleri kullanir"
Require-Text $mainWindowCode 'point.Y > VideoBorder.ActualHeight' "R4: Fare video disindayken kontroller acilmaz"
Require-Text $mainWindowCode 'ShowOverlayPlaybackControls(force: true);' "R4: F6 video disindayken de kontrolleri acar"
Require-Text $mainWindow 'x:Key="PlayerStudioColumnStyle"' "R4: CC2 modern cift kolon karti"
Require-Text $mainWindow 'x:Key="PlayerStudioSliderStyle"' "R4: Android tarzi ince cyan slider"
Require-Text $mainWindow 'Style="{StaticResource PlayerStudioSliderStyle}"' "R4: CC2 slider stili uygulandi"
$studioSliderUses = [regex]::Matches($mainWindow, 'Style="\{StaticResource PlayerStudioSliderStyle\}"').Count
if ($studioSliderUses -ne 8) { throw "R4: CC2 (4) ve video ayarlari (4) slider stili eksik: $studioSliderUses" }
Require-Text $mainWindow 'Width="610" MaxHeight="660"' "R4: Merkez ayarlari daha ferah bir masaustu enine sahip"
Require-Text $mainWindowCode 'PlaybackSettingsSheetPanel.MaxHeight = Math.Min(660' "R4: Ayarlar ekrana sigan responsive yukseklikte"
$overlayPopupTagIndex = $mainWindow.LastIndexOf('<Popup ', $overlayPopupIndex)
if ($overlayPopupTagIndex -lt $overlayDeckIndex) { throw "R4: Overlay Popup ayirici bulunamadi" }
$deckToPopup = $mainWindow.Substring($overlayDeckIndex, $overlayPopupTagIndex - $overlayDeckIndex)
Reject-Text $deckToPopup 'x:Name="SeekPreviewPopup"' "R4: Native seek onizleme hostu degistirilen deck icinde degil"
Reject-Text $deckToPopup '<Popup ' "R4: Merkez ayarlari ve altyazi popup'lari tek deck icine gomulu degil"
Require-Text $mainWindow 'x:Name="PlaybackCenterSheet"' "R4: Merkez oynatma ayarlari korunuyor"
Require-Text $mainWindow 'x:Name="DualSubtitleCenterSheet"' "R4: CC2 merkezi korunuyor"
Require-Text $mainWindow 'x:Name="MiniPlayerControlsPopup"' "R4: Mini Player bagimsiz overlay"
Require-Text $mainWindow 'x:Name="FullscreenControlsPopup"' "R4: Tam ekran bagimsiz overlay"

# R4.1: focused center sheets hide the underlying controls, regardless of
# overlay pin state. Preserve the R4 narrow-window dock fallback.
Require-Text $mainWindow 'Unchecked="CenterSheetToggle_OnUnchecked"' "R4.1: Merkez sheet kapanisi baglandi"
Require-Text $mainWindowCode 'private void CenterSheetToggle_OnUnchecked' "R4.1: Kapanista kontrol cubugu geri yuklenir"
Require-Text $mainWindowCode 'private void UpdateCenterSheetChrome()' "R4.1: Center sheet gorsel odagi"
Require-Text $mainWindowCode 'if (IsCenterSheetOpen)' "R4.1: Panel acikken alt kontroller gizlenir"
Require-Text $mainWindowCode 'HideOverlayPlaybackControls();' "R4.1: Overlay cam bar gizlenebilir"
Require-Text $mainWindowCode 'PlaybackControlDeck.Visibility = Visibility.Collapsed;' "R4.1: Dar pencerede dock bar da gizlenir"
Require-Text $mainWindowCode 'PlaybackControlDeck.Visibility = Visibility.Visible;' "R4.1: Sheet kapandiginda dock bar geri gelir"
Require-Text $mainWindowCode 'IsCenterSheetOpen)' "R4.1: Video hover acilisinda panel odagi korunur"
Require-Text $mainWindow 'Width="44" Height="44" MinWidth="44" Content="{Binding PlayPauseText}"' "R4.1: Compact video transport"
# R4.1.1: Mini/fullscreen have exactly one HUD; restore normal controls
# after exiting without creating a second visual parent.
Require-Text $mainWindowCode 'private void SuspendNormalPlaybackDeck()' "R4.1.1: Compact modda normal deck askida"
Require-Text $mainWindowCode 'private void RestoreNormalPlaybackDeck()' "R4.1.1: Normal modda tek deck geri baglanir"
Require-Text $mainWindowCode 'OverlayPlaybackControlsPopup.Child = null;' "R4.1.1: Normal overlay popup cocugu ayriliyor"
Require-Text $mainWindowCode 'RootLayoutGrid.Children.Remove(PlaybackControlDeck);' "R4.1.1: Sabit alt bar Mini moddan ayriliyor"
Require-Text $mainWindowCode 'if (_switchingDeckMode || _playbackDeckSuspended) return;' "R4.1.1: Mini modda reparent yeniden tetiklenmez"
Require-Text $mainWindowCode 'PlaybackControlDeck.Visibility = Visibility.Collapsed;' "R4.1.1: Normal dock Mini modda gorunmez"
Require-Text $mainWindowCode 'RootLayoutGrid.Children.Add(PlaybackControlDeck);' "R4.1.1: Cikista tek ebeveyn geri yuklenir"
Require-Text $mainWindowCode 'RestoreNormalPlaybackDeck();' "R4.1.1: Mini/tam ekrandan cikis guvenli"
$miniToggle = [regex]::Match($mainWindowCode, '(?s)private void ToggleMiniPlayer\(\).*?private void SaveWindowPresentationState\(')
$fullToggle = [regex]::Match($mainWindowCode, '(?s)private void ToggleFullscreen\(\).*?private void ToggleMiniPlayer\(')
if (-not $miniToggle.Success -or -not $fullToggle.Success) { throw "R4.1.1: Mini/Fullscreen lifecycle eksik" }
Require-Text $miniToggle.Value 'SuspendNormalPlaybackDeck();' "R4.1.1: Mini giriste deck askida"
Require-Text $miniToggle.Value 'RestoreNormalPlaybackDeck();' "R4.1.1: Mini cikista deck geri geliyor"
Require-Text $fullToggle.Value 'SuspendNormalPlaybackDeck();' "R4.1.1: Tam ekran giriste deck askida"
Require-Text $fullToggle.Value 'RestoreNormalPlaybackDeck();' "R4.1.1: Tam ekran cikista deck geri geliyor"
# Additional playback actions appear in the SAME editable shortcut grid.
Require-Text $mainWindow 'Player.Shortcuts.ExtraTitle' "R4.1.1: Ek kisayol basligi ADB Player'a ait"
Require-Text $mainWindow 'Player.Shortcuts.ExtraHelp' "R4.1.1: None ile devre disi birakma aciklamasi"
Require-Text $mainWindowCode 'private bool HandleConfiguredExtraShortcut(Key key, ModifierKeys modifiers)' "R4.1.1: Ek tuslar ayarlardan okunur"
Require-Text $mainWindowCode 'HandleConfiguredExtraShortcut(key, modifiers)' "R4.1.1: Temel atamalardan sonra ek tuslar"
Reject-Text $mainWindow 'VlcAliases' "R4.1.1: Sabit dis uygulama ipucu karti kaldirildi"
Reject-Text $mainWindowCode 'HandleVlcConvenienceKey' "R4.1.1: Sabit dis uygulama tuslari yok"
Require-Text $mainWindow 'ShortcutSpeedIncrease' "R4.1.1: SpeedIncrease duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.SpeedIncrease' "R4.1.1: SpeedIncrease ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutSpeedDecrease' "R4.1.1: SpeedDecrease duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.SpeedDecrease' "R4.1.1: SpeedDecrease ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutSpeedReset' "R4.1.1: SpeedReset duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.SpeedReset' "R4.1.1: SpeedReset ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutNextMedia' "R4.1.1: NextMedia duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.NextMedia' "R4.1.1: NextMedia ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutCycleAudioTrack' "R4.1.1: CycleAudioTrack duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.CycleAudioTrack' "R4.1.1: CycleAudioTrack ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutCycleSubtitleTrack' "R4.1.1: CycleSubtitleTrack duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.CycleSubtitleTrack' "R4.1.1: CycleSubtitleTrack ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutMute' "R4.1.1: Mute duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.Mute' "R4.1.1: Mute ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutVolumeIncrease' "R4.1.1: VolumeIncrease duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.VolumeIncrease' "R4.1.1: VolumeIncrease ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutVolumeDecrease' "R4.1.1: VolumeDecrease duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.VolumeDecrease' "R4.1.1: VolumeDecrease ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutScreenshotAlternate' "R4.1.1: ScreenshotAlternate duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.ScreenshotAlternate' "R4.1.1: ScreenshotAlternate ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutSubtitleDelayDecreaseAlternate' "R4.1.1: SubtitleDelayDecreaseAlternate duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.SubtitleDelayDecreaseAlternate' "R4.1.1: SubtitleDelayDecreaseAlternate ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutSubtitleDelayIncreaseAlternate' "R4.1.1: SubtitleDelayIncreaseAlternate duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.SubtitleDelayIncreaseAlternate' "R4.1.1: SubtitleDelayIncreaseAlternate ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutAudioDelayDecreaseAlternate' "R4.1.1: AudioDelayDecreaseAlternate duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.AudioDelayDecreaseAlternate' "R4.1.1: AudioDelayDecreaseAlternate ayarlar uzerinden eslenir"
Require-Text $mainWindow 'ShortcutAudioDelayIncreaseAlternate' "R4.1.1: AudioDelayIncreaseAlternate duzenlenebilir alan"
Require-Text $mainWindowCode 'PlayerShortcutAction.AudioDelayIncreaseAlternate' "R4.1.1: AudioDelayIncreaseAlternate ayarlar uzerinden eslenir"

if ($Build) {
    $solution = Join-Path $root "AltyaziDB.Player.Windows.sln"
    $generator = Join-Path $root "tool\generate-app-credentials.ps1"
    $trackedCloudConfig = Join-Path $root "src\AltyaziDB.Player.Infrastructure\Generated\GeneratedAdbCloudConfig.g.cs"

    if (-not (Test-Path -LiteralPath $generator)) {
        throw "Kimlik uretim betigi bulunamadi: $generator"
    }

    # Clean worktrees do not contain the ignored GeneratedAppCredentials.g.cs file.
    # Match the real release pipeline: generate local credentials before compiling.
    # Preserve the tracked, non-secret ADB Cloud config byte-for-byte so this verifier
    # does not leave the working tree dirty after a developer build.
    $trackedCloudConfigExisted = Test-Path -LiteralPath $trackedCloudConfig
    $trackedCloudConfigBackup = if ($trackedCloudConfigExisted) {
        [IO.File]::ReadAllBytes($trackedCloudConfig)
    } else {
        $null
    }

    try {
        Write-Host "Yerel build kimlik dosyalari uretiliyor..." -ForegroundColor Cyan
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $generator
        if ($LASTEXITCODE -ne 0) {
            throw "generate-app-credentials.ps1 basarisiz oldu: exit $LASTEXITCODE"
        }

        $generatedCloudCredentials = Join-Path $root "src\AltyaziDB.Player.Cloud\Generated\GeneratedAppCredentials.g.cs"
        if (-not (Test-Path -LiteralPath $generatedCloudCredentials)) {
            throw "Bulut kimlik kaynak dosyasi uretilemedi: $generatedCloudCredentials"
        }
        Write-Host "[OK] Build kimlik kaynaklari hazir" -ForegroundColor Green

        Write-Host "Release x64 derleme baslatiliyor..." -ForegroundColor Cyan
        & dotnet build $solution --configuration Release -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz oldu: exit $LASTEXITCODE" }
        Write-Host "[OK] Release x64 build" -ForegroundColor Green
    }
    finally {
        if ($trackedCloudConfigExisted) {
            [IO.File]::WriteAllBytes($trackedCloudConfig, $trackedCloudConfigBackup)
        }
        elseif (Test-Path -LiteralPath $trackedCloudConfig) {
            Remove-Item -LiteralPath $trackedCloudConfig -Force
        }
    }
}

Write-Host "ADB Player V1.0.2 Feedback R1 dogrulamasi tamamlandi." -ForegroundColor Cyan
