# Subtitle Sync v2 — Original Audio + Multi-Window Timeline

Bu revizyon otomatik altyazı senkronunu yalnızca ffsubsync sonucuna bağlı olmaktan çıkarır.

## Pipeline

1. Original Audio Resolver gömülü sesler arasından özgün sesi seçer.
2. FFmpeg seçilen sesi 8 kHz mono PCM zaman çizelgesine dönüştürür.
3. ADB Subtitle Timeline v2 konuşma aktivitesi ve konuşma başlangıçlarını çıkarır.
4. Altyazı cue zaman çizelgesi 7–9 ayrı pencerede ses zaman çizelgesiyle karşılaştırılır.
5. Yerel lag noktalarından Fixed, FrameRate/Drift veya Piecewise model seçilir.
6. Altyazı yalnız zaman kodları değiştirilerek yeni çalışma dosyasına yazılır.
7. Çıktı farklı noktalarda residual verification ile yeniden ölçülür.
8. Mevcut Subtitle Sync Safe Apply Gate yapısal bozulmayı ayrıca kontrol eder.
9. v2 güven oluşturamazsa mevcut ffsubsync motoru fallback olarak denenir.

## Original Audio Resolver

Kullanıcının oynattığı ses parçası zorunlu referans değildir.

- `Original`, `Original Audio`, `OV` gibi etiketler güçlü öncelik alır.
- Commentary / Audio Description parçaları elenir.
- Dub / Dublaj etiketleri geriye atılır.
- Türkçe + Türkçe dışı çoklu ses içeriğinde Türkçe dışı gömülü track özgün ses adayı olarak öne çıkar.
- MPV `default` bayrağı ek sinyal olarak kullanılır.
- Son eşitlikte container sırası korunur.

Bu sayede kullanıcı Türkçe dublajı dinlerken altyazı senkronu arka planda videonun özgün gömülü sesini kullanabilir.

## Güvenlik

Bir sonuç sadece ffmpeg/analiz tamamlandı diye uygulanmaz.

V2 doğrulama:
- en az dört güvenilir probe,
- yeterli pencere coverage,
- düşük medyan residual,
- düşük yüksek-percentil residual,
- yeterli korelasyon skoru

ister.

Bunların ardından mevcut Safe Apply Gate ayrıca cue sayısı, overlap, concurrent cue ve süre bozulmasını kontrol eder.

Her iki kapı da geçilmeden `Synchronized` sonucu Player'a uygulanmaz.


## Bilinen FPS dönüşümleri

Timeline v2, serbest lineer drift ölçümüne ek olarak yaygın sinema/TV cadence dönüşümlerini kanonik oranlara sabitler.

Özellikle 25 FPS altyazı → 23.976 FPS video için zaman ölçeği yaklaşık `1.042708333x` olur. Yaklaşık 48:32 uzunluğundaki içerikte bu fark sona doğru 124 saniyeyi aşar; bu nedenle v2 arama penceresi içerik süresine göre dinamik olarak genişletilir ve eski 120 saniyelik sabit sınırla kesilmez.

Desteklenen kanonik adaylar:
- 25 → 23.976
- 25 → 24
- 24 → 23.976
- ters yönleri

Model ancak çoklu pencere ölçümleri aynı oranı yeterince düşük residual ile doğrularsa bu FPS dönüşümüne kilitlenir.


## Canonical cadence hypothesis pass

Production logs showed that unconstrained per-window lag search could lock onto different speech regions and incorrectly classify a smooth FPS drift as many Piecewise edit boundaries.

The engine now tests known FPS conversions before generic Piecewise analysis:

1. scale subtitle cue times by each known cadence candidate;
2. find one global offset across the whole programme from distributed cue onsets;
3. run independent multi-window residual verification;
4. accept the cadence only if normal verification passes;
5. only then fall back to unconstrained Fixed/Drift/Piecewise analysis.

This specifically prevents a true 25 -> 23.976 drift from becoming a false seven-split Piecewise model.
