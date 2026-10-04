# Subtitle Sync Safe Apply Gate

Bu revizyon otomatik altyazı senkronunun yanlış bir ffsubsync çıktısını oynatıcıya uygulamasını engeller.

## Referans ses

ADB Player artık ffsubsync çağrısına açıkça `--reference-stream a:N` verir.

- seçili ses gömülü ise onun audio ordinal'i kullanılır;
- harici ses seçiliyse videonun ilk gömülü ses parçası kullanılır;
- böylece ffsubsync aynı dosyadaki İngilizce/SDH gömülü altyazıyı istemeden referans olarak seçemez.

## Safe Apply Gate

Senkron çıktısı uygulanmadan önce kaynak ve çıktı zaman çizelgeleri karşılaştırılır:

- cue sayısı,
- geçersiz zaman aralıkları,
- yeni overlap sayısı,
- toplam overlap süresi,
- aynı anda aktif cue sayısı,
- medyan ve maksimum cue süresi.

Çıktı yapısal olarak kötüleşirse senkronlu dosya silinir, Player mevcut çalışma kopyasını/orijinal altyazıyı kullanmaya devam eder ve sonuç `Uncertain` kabul edilir.

Bu kontrol özellikle çok satırlı/üst üste binen yanlış ffsubsync sonuçlarının ekrana uygulanmasını engellemek içindir.
