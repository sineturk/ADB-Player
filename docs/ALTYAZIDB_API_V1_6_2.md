# AltyazıDB API v1.6.2 istemci sözleşmesi

Build W3, Windows istemcisini `api.php` ve `api-docs.php` kaynaklarındaki gerçek v1 davranışına göre yapılandırır.

## Temel kurallar

- Temel adres: `https://altyazidb.com/api/v1`
- Tüm isteklerde `X-API-Key` başlığı zorunludur.
- API yalnız HTTPS kabul eder.
- Anahtar Windows DPAPI ile kullanıcı hesabına bağlı şifreli depoda saklanır.
- `/me` ile kullanıcı rolü ve dakika/saat/gün kotaları doğrulanır.

## Arama

`GET /search`

Gönderilebilen alanlar:

- `imdb_id`, `tmdb_id` veya `title`
- `year`
- `content_type=movie|series`
- `version_group`
- `lang`
- `season`
- `episode` veya sezon paketi için `PAKET`
- `page`, `limit`, `sort=date|downloads`

İstemci `download_url` alanını öncelikli kullanır. API, istenen bölüm bir sezon paketindeyse bu alanı otomatik olarak `/extract_ep` adresine dönüştürür. `archive_url` yalnız özgün arşiv gerektiğinde saklanır.

## Yanıt alanları

W3 aşağıdaki alanları ayrıştırır:

- sayfalama ve `filters.version_groups`
- içerik eşleştirme yöntemi (`search_method`)
- `duration` / `sure_bilgisi`
- `versions`, `releases`, `version_groups`
- çevirmen, yükleyen, indirme sayısı ve tarih
- paket/CC/forced/AI/yabancı kısımlar bayrakları
- `stream_url`, `download_url`, `archive_url`

## Hata davranışı

- 401: eksik, geçersiz veya pasif anahtar
- 403: yetki reddi
- 429: hız sınırı
- 5xx ve geçici ağ hataları: sınırlı yeniden deneme

API anahtarı kaynak koda veya `settings.json` içine yazılmaz.

## Kaynak kod uyumluluk notu

Sunucu kodu `version_group` / `source` parametresini okuyor, fakat mevcut sorguda bu değeri SQL koşuluna eklemiyor. Windows istemcisi parametreyi yine gönderir ve sonuç kümesini istemci tarafında ayrıca filtreleyerek arayüz davranışını tutarlı tutar.
