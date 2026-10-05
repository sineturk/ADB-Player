# ADB Player V1.0 yayın sistemi

## Üretilen dosyalar

- `artifacts/portable/`: self-contained Windows x64 uygulama klasörü
- `artifacts/release/ADB-Player-Portable-v1.0.1.0-x64.zip`
- `artifacts/installer/ADB-Player-Setup-v1.0.1.0-x64.exe`
- `artifacts/release/update-manifest.json`
- `artifacts/release/SHA256SUMS.txt`

Portable ve kurulum çıktılarında doğrulanmış `native\rclone\rclone.exe` bulunması zorunludur. Yayın betikleri dosya eksikse sabitlenen resmî paketi indirir ve SHA-256 doğrulaması yapar.

## Komutlar

```powershell
.\tool\publish-portable.ps1
.\tool\build-installer.ps1
.\tool\build-release.ps1 -InstallerBaseUrl "https://sunucu/releases" -PortableBaseUrl "https://sunucu/releases"
.\tool\verify-release.ps1
```

Kurulum EXE'si Inno Setup 7/6 derleyicisi (`ISCC.exe`) gerektirir. `build-installer.ps1`, standart kurulum yollarını ve PATH'i denetler.

## Güncelleme manifesti

Uygulamanın Güncelleme sekmesine `update-manifest.json` dosyasının HTTPS adresi girilir. İstemci kanal ve sürüm bilgisini denetler, kurulum EXE'sini HTTPS üzerinden indirir, SHA-256 karmasını doğrular ve yalnız doğrulama başarılıysa kurucuyu başlatır.

Manifestte gerçek yayın URL'leri ve SHA-256 değerleri bulunmadan güncelleme dağıtımı yapılmamalıdır.

## İsteğe bağlı kod imzalama

```powershell
$env:ALTYAZIDB_SIGNING_PASSWORD = "PFX_PAROLASI"
.\tool\sign-release.ps1 -PfxPath "C:\sertifika\adb-player.pfx" -TimestampUrl "ZAMAN_DAMGASI_URLSI"
```

Sertifika veya parola kaynak paketine yazılmaz. Alternatif olarak Windows sertifika deposundaki sertifikanın thumbprint değeri kullanılabilir.


## V1.0 Public Release Gate

Private geliştirme reposu public yapılmaz. Public kaynak, release commit'inden temizlenmiş tek commit'lik snapshot olarak üretilir.

Aday doğrulama:

```powershell
.\tool\verify-public-release-gate.ps1 -Mode Candidate
```

Stable public gate:

```powershell
.\tool\verify-public-release-gate.ps1 `
  -Mode Public `
  -InstallerBaseUrl "https://github.com/<owner>/<public-repo>/releases/download/v1.0.1" `
  -PortableBaseUrl "https://github.com/<owner>/<public-repo>/releases/download/v1.0.1" `
  -ManualAcceptanceConfirmed
```

Kod imzası hard gate olacaksa ayrıca:

```powershell
-RequireSignature `
-PfxPath "C:\sertifika\adb-player.pfx" `
-TimestampUrl "https://<timestamp-server>"
```

İmzalama sonrasında portable ZIP, installer, SHA256SUMS ve update manifest hash'leri yeniden üretilir.

Public source snapshot çıktısı:

```text
artifacts\public-source\ADB-Player-v1.0.1.0
```

Snapshot; araştırma/evidence arşivlerini, tarihî patch/build notlarını, backend deployment kaynaklarını ve private handoff belgelerini içermez.
