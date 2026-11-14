# ADB Player — Bulut hesabı bağlama (BYOC)

ADB Player, **Google Drive** için kullanıcıya ait OAuth uygulama kimliğini zorunlu tutar: rclone'un ortak Google Drive istemcisi 2026 içinde kullanımdan kaldırılıyor. **Dropbox, OneDrive ve pCloud** için kendi uygulama kimliğiniz önceliklidir; henüz oluşturmadıysanız rclone'un ortak sağlayıcı istemcisiyle devam etmek mümkündür. Ortak istemcilerde kota veya erişim sınırlamaları olabilir; bu bir ADB Player garantisi değildir.

## İki giriş noktası, tek güvenli kayıt

1. **Bulut ve diğer kaynaklar** sayfasında sağlayıcı kartını açın. Daha önce OAuth bilgisi kaydetmediyseniz **Uygulama kimlik bilgileri (BYOC)** bölümü açılır.
2. İlgili sağlayıcının **Kurulum rehberini aç** bağlantısından resmi rclone açıklamalarına gidin.
3. Sağlayıcınızın geliştirici panelinden kendi **Client ID (App Key)** ve **Client Secret (App Secret)** değerlerinizi oluşturun. ADB Player'a hesap parolanızı girmeyin.
4. Değerleri bu pencerede birlikte girin **veya** Ayarlar → **Bulut bağlantı bilgileri** bölümünde aynı sağlayıcıya kaydedin.
5. **Hesap bağla** düğmesini kullanın. Hesap oturumunu sağlayıcının kendi tarayıcı sayfasında açın. ADB Player, bağlantıyı yerel rclone ile doğruladıktan sonra hesabı listeler.

Google Drive'da iki alanın boş bırakılması **yalnızca** aynı sağlayıcının bilgilerinin Ayarlar'da önceden kaydedilmiş olduğu durumda geçerlidir. Diğer sağlayıcılarda iki alan da boşsa uygulama önceden kaydedilen bilgileri kullanır veya rclone'un sağlayıcı varsayılanına geçebilir. Bu giriş ekranında yeni bir ID/Secret girmeniz önceden kayıtlı sağlayıcı varsayılanını kalıcı olarak değiştirmez. Her yeni bulut hesabı, başarıyla bağlandıktan sonra seçilen OAuth uygulama kimliğine ayrıca bağlanır.

## Sağlayıcılar

| Sağlayıcı | Rehber | Not |
|---|---|---|
| Google Drive | https://rclone.org/drive/#making-your-own-client-id | Google Cloud'da Drive API ve masaüstü OAuth istemcisini hazırlayın. `drive.readonly` izniyle ilgili Google doğrulama ve test modu sınırlamalarını inceleyin. rclone'un ortak Google Drive kimliği 2026 içinde kullanımdan kaldırılıyor. |
| Dropbox | https://rclone.org/dropbox/#get-your-own-dropbox-app-id | Dropbox App Console'dan App Key ve App Secret alın; gerekli izinler ve `http://localhost:53682/` yönlendirme adresini yapılandırın. |
| OneDrive | https://rclone.org/onedrive/#getting-your-own-client-id-and-key | Microsoft Entra uygulama kaydı oluşturun; istemci kimliği ve gerekli secret bilgisini alın, geri yönlendirme URI'sini rclone rehberine göre ayarlayın. |
| pCloud ABD / Avrupa | https://rclone.org/pcloud/ | Bölgenize uygun pCloud uygulama kimliğini kullanın. pCloud'un yeni uygulama oluşturma erişimi bazı hesaplarda kısıtlı olabilir; geliştirici panelini veya sağlayıcı desteğini kontrol edin. İki bölge aynı sağlayıcı ayarını paylaşır. |

**Google OAuth test modu:** Google'ın OAuth ekranı Testing aşamasındaysa (Drive kapsamları kullanılırken) yenileme belirteçlerinin süresi kısalabilir. Yayın/Doğrulama durumunu ve kullanıcı limitlerini Google'ın güncel politikalarına göre kontrol edin. Kendi OAuth uygulamanızı oluşturmanız tüm sağlayıcı politikalarından otomatik muafiyet sağlamaz.

## Güvenlik ve hesap yönetimi

- Client ID ve Secret ile erişim/yenileme belirteçleri kullanıcının cihazındaki korumalı depoda tutulur; ADB Cloud hesabı zorunlu değildir.
- Oturum açma bağlantıları ve tokenlar uygulama günlüklerinde açıkça yazdırılmaz.
- Mevcut bağlantılar bu sürümde otomatik silinmez veya yeni kimliğe zorla taşınmaz. Önceden ortak rclone kimliğiyle bağlanan Google Drive hesapları için kontrollü yeniden yetkilendirme gerekebilir.
- ADB Player bu alanlara hesap parolası veya Google Cloud Service Account JSON anahtarları istemez.
- Bir OAuth uygulamasının yetkisini kaldırmak isterseniz ilgili sağlayıcının hesap güvenliği sayfasından uygulama erişimini de iptal edin.

Bu belge v1.0.2 RC2 P4.16.4 kapsamındaki kullanıcı bağlantı rehberidir. Public yayın öncesinde sağlayıcı paneli adları ve bağlantı adımları tekrar kontrol edilmelidir.
