# Harici Bağlantılar

Bağlantılar paneli yalnız kullanıcının kendi kurduğu veya kullanmaya yetkili olduğu hizmetlere doğrudan bağlanır. AltyazıDB sunucuları sorgulara aracılık etmez.

## Prowlarr

Sunucu kök adresini ve Prowlarr ayarlarından alınan API anahtarını girin. Yerel varsayılan örnek adres `http://localhost:9696` olabilir; yerel HTTP kutusunun ayrıca işaretlenmesi gerekir. Uzak kurulumlarda HTTPS kullanın.

## Generic Torznab

Indexer veya kullanıcının kendi Prowlarr Torznab endpoint'inin tam API adresini girin. API anahtarını ayrı alana yazın. Film, dizi, anime ve diğer Torznab kategorileri virgülle ayrılır.

## Sonarr ve Radarr

Sunucu kök adresi ve API anahtarıyla bağlantı/kütüphane erişimi doğrulanır. Bu iki bağlantı Son Torrentler aramasına katılmaz.

## Saklama

Gizli bağlantı payload'ı Windows DPAPI ile şifrelenir. Profil listesinde API anahtarı ve gerçek URL yer almaz. Bağlantı silindiğinde gizli kayıt da kaldırılır.
