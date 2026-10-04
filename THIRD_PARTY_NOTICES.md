# ADB Player — Üçüncü Taraf Bildirimleri

ADB Player aşağıdaki üçüncü taraf bileşenlerden yararlanır:

## rclone

- Proje: rclone
- Sürüm: 1.74.4 Windows amd64
- Lisans: MIT
- Telif: Copyright (C) 2012 Nick Craig-Wood
- Lisans metni: `native/rclone/LICENSE.txt`

Rclone çalışma zamanı resmî `downloads.rclone.org` dağıtımından alınır ve sabit SHA-256 değeriyle doğrulanır.

## mpv / libmpv

Oynatma motoru olarak libmpv kullanılır. Dağıtımda kullanılan derlemenin kendi lisans bildirimleri yayın paketinde korunmalıdır.

## TDLib

Telegram özelliği `tdlib.native.win-x64` 1.8.66 NuGet paketindeki TDLib `tdjson.dll`, OpenSSL ve zlib çalışma dosyalarını kullanır. Paket lisans ifadesi BSL-1.0, Apache-2.0 ve Zlib lisanslarını kapsar; NuGet paketindeki bildirimler yayın çıktısında korunur.

## MonoTorrent

Torrent akışı `MonoTorrent` 3.0.2 NuGet paketini kullanır. Bileşen MIT lisanslıdır. Yerleşik HTTP Range akışı, oynatıcının beklemeden başlamasını ve seek konumundaki torrent parçalarının önceliklendirilmesini sağlar.

## SharpCompress

Sıkıştırılmış ZIP, RAR ve 7-Zip arşivlerindeki medyayı oynatma sırasında çözmek için `SharpCompress` 0.50.1 kullanılır. Bileşen MIT lisanslıdır.

## Microsoft.Data.Sqlite / SQLitePCLRaw

Yerel video kütüphanesi ve izleme geçmişi için SQLite bileşenleri kullanılır.

## Anime4K GLSL shaders

- Project: Anime4K
- Copyright: Copyright (c) 2019 bloc97
- License: MIT
- Source commit: `7684e9586f8dcc738af08a1cdceb024cc184f426`
- Usage: Optional real-time GLSL image enhancement through libmpv.

The complete license text is distributed in `native/anime4k/LICENSE.txt`. Some AutoDownscale shader files additionally identify themselves as public-domain software in their own headers.

## AudioSyncTool — embedded analysis engine
- Project: https://github.com/blast1see/AudioSyncTool
- Reference version: v2.5.0
- License: MIT
- Use in ADB Player: v2.5.0 `AudioAnalyzer` is executed headlessly by an isolated embedded Python runtime. The upstream GUI is not launched. The previous .NET analyzer remains only as a fallback.
- License copy: `docs/licenses/AudioSyncTool-LICENSE.txt`

## FFmpeg Windows build for audio sync
- FFmpeg project: https://ffmpeg.org/
- Distribution source: https://github.com/mifi/ffmpeg-builds/releases/tag/8.0-1
- Windows x64 package SHA-256: `0836cf94503b3497bff0497eebda4e74f34017ab6d85c2d8e69f3f9900d5a496`
- FFmpeg is optional at development time; automatic audio synchronization is unavailable when the executable is not present.


## Python / NumPy / SciPy runtime for AudioSyncTool
- Python 3.12.10 embeddable x64 is downloaded from python.org.
- NumPy 2.2.6 and SciPy 1.15.3 Windows x64 wheels are downloaded from PyPI and SHA-256 verified.
- Their upstream license files/dist-info metadata remain inside `native/audiosynctool/python`.
- This runtime is isolated under the Player native directory and does not install Python system-wide.


## ffsubsync
- Upstream: https://github.com/smacke/ffsubsync
- Integrated version: 0.5.1
- License: MIT
- License copy: `docs/licenses/ffsubsync-LICENSE.txt`
- Used as an isolated Windows subtitle synchronization runtime; ADB Player does not modify subtitle text, only timing.

## ArtCNN
- Upstream: https://github.com/Artoriuz/ArtCNN
- Integrated reference release: v1.6.2
- License: MIT
- License copy: `docs/licenses/ArtCNN-LICENSE.txt`
- Runtime shaders: official `GLSL/ArtCNN_C4F16.glsl` and `GLSL/ArtCNN_C4F32.glsl`.
- The installer downloads the pinned shader files from the v1.6.2 tag and verifies their Git blob SHA-1 before use.
- C4F16 is the lightweight real-time option; C4F32 is an explicit higher-quality / higher-GPU-load option.

