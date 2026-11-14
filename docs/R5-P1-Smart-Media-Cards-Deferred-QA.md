# ADB Player Windows v1.0.2 — R5 / P1 Smart Media Cards

> PRIVATE geliştirme notu. Kullanıcı isteğiyle P1 sırasında Windows CI, otomatik test ve görsel kabul **çalıştırılmadı**. P0 başarılı baz: `70c32c38002f2e8e7a0aa033fb665138429060f0`.

## Kapsam

- `ContinueWatchingItem`: kaynak kimliği korunarak doğrulanmış sezon/bölüm, süre, kalan süre ve ilerleme etiketleri.
- `HomeResumeGroup`: yalnız güvenilir bölüm kimliği olan aynı diziyi tek görsel kartta toplama; bölüm kayıtları silinmez.
- `HomeView`: grup kartı, ayrı bölüm seçim menüsü; son izleme devamı mevcut kaynak üzerinden açılır.
- `ContinueWatchingArtworkPolicy`: yapım türü, başlık, eşleşme güveni ve yıl kontrolü; yalnız HTTPS görsel URL'leri.
- `MediaArtworkService`: Home için doğrulanmış görsel önbelleği işareti; eski/doğrulanmamış kayıtlar doğrudan kullanılmaz.
- P1 değişiklikleri yalnız `feature/v1.0.2-r5-cinematic-home-p1` dalında. Public yayın yok.

## Toplu QA aşamasında yapılacaklar (henüz çalıştırılmadı)

1. P0/P1 kaynak doğrulama betikleri; Windows .NET 10 Release x64 CI; libmpv ve SHA-256 artifact.
2. 1180×600, 1500×860, yüksek DPI: hero, 16:9 medya kartları, yatay ray, bölüm menüsü ve klavye erişimi.
3. Aynı dizinin S01E01/S01E02/S02E01 kayıtları: tek seri kartı, üç ayrı bölüm seçimi, her birinin doğru `Source` ve konumla açılması.
4. Aynı bölümün iki farklı release'i: iki ayrı seçilebilir kaynak; otomatik yanlış dosya seçilmemesi.
5. Aynı adı taşıyan farklı yıllardaki iki yapım: yanlış grup ve yanlış poster eşleşmesi olmaması.
6. Film başlığı, yıl, içerik türü veya yüksek güven skoru tutarsızsa görselin reddedilmesi; boş görselde ADB varsayılan kartı.
7. Cache durumları: eski görsel cache; güncel doğrulanmış cache; çevrimdışı/oturum kapalı; API başarısız ve görselsiz sonuç.
8. Geçerli yerel dosya, mevcut bulut URL'si, kaybolmuş yerel dosya ve geçersiz/sona ermiş URL kaynakları.
9. Ana sayfa dışı regresyon: bulut hesapları, bağlantılar, video HUD, CC², altyazı/ses senkronu ve Mini Player birlikte gizlenme.
10. Tekrar açılışta resume geçmişinin ve saklanan hesapların değiştirilmediğinin doğrulanması.

## Sonraki iş

P2 — Sources & Connections; P3 — Glass, Motion ve QA. Bu aşamalardan sonra toplu Windows testleri ve ekran görüntüsü kabulü gerçekleştirilecek. Public depoya yayın için ayrı kullanıcı onayı alınacak.
