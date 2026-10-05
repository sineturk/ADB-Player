# ADB Player

ADB Player; .NET 10, WPF ve libmpv üzerinde geliştirilen Windows 10/11 x64 medya oynatıcısı ve medya merkezi uygulamasıdır.

V1.0; yerel oynatma, kullanıcının kendi bağladığı kaynaklar, altyazılar, cihazlar arası profil geri yükleme ve zamanlaması uyuşmayan harici ses/al­tyazılar için güvenli manuel senkron akışına odaklanır.

## Öne çıkanlar

- libmpv tabanlı yerel video oynatma.
- Doğrudan URL, WebDAV/NAS ve desteklenen bulut kaynağı akışları.
- İsteğe bağlı Telegram entegrasyonu.
- Kullanıcının eklediği Stremio uyumlu eklentiler ve harici indexer/kaynak bağlantıları.
- ADB Cloud ile hesap, izleme ilerlemesi, koleksiyonlar, oynatma kuyruğu ve taşınabilir ayar senkronu.
- Yeni cihazda desteklenen eklenti profillerinin otomatik geri yüklenmesi.
- Resmî derlemelerde AltyazıDB altyazı arama/entegrasyonu.
- Güven ve Safe Apply Gate katmanlarıyla harici ses ve altyazı senkronu.
- Türkçe ve İngilizce arayüz.
- Installer ve portable Windows x64 dağıtımları.
- Resmî stable GitHub akışı üzerinden HTTPS + SHA-256 doğrulamalı uygulama içi güncelleme.

ADB Player; hazır medya kataloğu, torrent indexer'ı, telifli medya, üçüncü taraf hesap kimliği veya önceden tanımlı içerik kaynağı **sunmaz**. Kullanıcı bağladığı hizmet ve içeriklerden sorumludur.

## Sistem gereksinimleri

- Windows 10 build 19041 veya üzeri ya da Windows 11.
- x64 işlemci.
- Çevrimiçi özellikler için internet bağlantısı.
- GPU hızlandırması isteğe bağlıdır; seçilen oynatma profiline göre uygun fallback kullanılır.

## Resmî dağıtımlar

Public sürümde şu çıktılar yayınlanır:

- `ADB-Player-Setup-v1.0.1.0-x64.exe`
- `ADB-Player-Portable-v1.0.1.0-x64.zip`
- `SHA256SUMS.txt`

Kurulumdan önce SHA-256 değerlerini doğrulayın. Kod imzalı bir paket yayınlandıysa Windows imzasının da geçerli olması gerekir.

## Kaynaktan derleme

Geliştirici gereksinimleri:

- Windows 10/11 x64
- .NET 10 SDK
- PowerShell
- Git

Repo kökünde:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\tool\build-debug.ps1
.\tool\run-debug.ps1
```

Kimlik bilgileri verilmezse build araçları secret içermeyen fallback sınıfları üretir. Telegram, sağlayıcı OAuth, AltyazıDB API ve ADB Cloud gibi isteğe bağlı entegrasyonlar geliştiricinin `.env.local` veya ortam değişkenleriyle verdiği yapılandırmayı gerektirir. Gerçek kimlik bilgileri Git'e eklenmemelidir.

Public kaynak checkout'u için kaynak doğrulayıcı:

```powershell
.\tool\verify-public-source.ps1
```

Yayın paketleri:

```powershell
.\tool\publish-portable.ps1
.\tool\build-installer.ps1
.\tool\verify-release.ps1
```

Installer üretimi için Inno Setup 6 veya 7 gerekir.

## Veri konumları

Kurulu sürüm kullanıcı verisini şu alanda tutar:

```text
%LOCALAPPDATA%\ADB\Player
```

Portable sürüm uygulama klasöründeki `data\` dizinini kullanır.

ADB Player desteklenen eski yerel verileri mümkün olduğunda yeni konuma taşır.

## Public kaynak kapsamı

V1.0 public kaynak snapshot'ı ürün kaynak kodunu ve ADB Player'ı incelemek/derlemek için gerekli build-yayın araçlarını içerir. İç araştırma kanıtları, tarihî patch arşivleri, backend deployment kaynakları ve geliştirme handoff belgeleri özellikle dışarıda bırakılır.

## Gizlilik, güvenlik ve lisans

- [Gizlilik](PRIVACY.md)
- [Güvenlik](SECURITY.md)
- [Üçüncü taraf bildirimleri](THIRD_PARTY_NOTICES.md)
- [GPL-3.0 lisansı](LICENSE)

## Sürüm

[ADB Player V1.0.1 sürüm notları](RELEASE_NOTES_v1.0.1.md).
