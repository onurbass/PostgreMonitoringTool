# PgMonitor Kullanım Kılavuzu

PgMonitor, ortak bir PostgreSQL cluster'ında **lock'a takılan sorguları** yakalayan bir Windows uygulamasıdır.
Sorunu yakaladığında:

- kimin kimi blokladığını ve zincirin başındaki session'ı (kök bloklayıcı) bulur,
- kanıtı bilgisayarındaki bir dosyaya kaydeder,
- e-posta ile haber verir.

DB ekibi session'ları sonlandırdıktan sonra da kayıtlı kanıtı inceleyebilirsin.

> Subtrans (`SubtransSLRU` / `SubtransBuffer`) izleme ikinci aşamadır, bu sürümde yoktur.

---

## İçindekiler

1. [Kısaca nasıl çalışır](#1-kısaca-nasıl-çalışır)
2. [Güvenlik: uygulama neyi yapar, neyi asla yapmaz](#2-güvenlik-uygulama-neyi-yapar-neyi-asla-yapmaz)
3. [Kurulum](#3-kurulum)
4. [İlk açılış: demo modu](#4-ilk-açılış-demo-modu)
5. [Canlı izlemeye geçiş](#5-canlı-izlemeye-geçiş)
6. [E-posta ayarları](#6-e-posta-ayarları)
7. [Eşikler ve uygulama adları](#7-eşikler-ve-uygulama-adları)
8. [Pano ekranını okumak](#8-pano-ekranını-okumak)
9. [Bir olayı sonradan incelemek](#9-bir-olayı-sonradan-incelemek)
10. [E-postayı okumak](#10-e-postayı-okumak)
11. [Laptop'ta çalıştırmak](#11-laptopta-çalıştırmak)
12. [Veriler nerede, ne kadar yer kaplar](#12-veriler-nerede-ne-kadar-yer-kaplar)
13. [Sorun giderme](#13-sorun-giderme)
14. [Şirkette ilk kullanım kontrol listesi](#14-şirkette-ilk-kullanım-kontrol-listesi)
15. [Bilinen sınırlar](#15-bilinen-sınırlar)

---

## 1. Kısaca nasıl çalışır

- **Örnek alma:** Uygulama varsayılan olarak 10 saniyede bir `pg_stat_activity`'den session listesini okur. Olay
  sırasında bu aralık 5 saniyeye iner. İkisi de Ayarlar'dan değiştirilebilir (en az 5 sn ve 2 sn).
- **Lock zinciri:** En uzun bekleyen en fazla 50 session için PostgreSQL'e "seni kim blokluyor?" diye sorar
  (`pg_blocking_pids`). Zincir en eski bekleyenlerden kurulduğu için kök bloklayıcıyı bulmaya bu yeter.
- **Olay açılması:** Bir sorgu **30 saniyeden uzun** lock bekliyorsa ve bu durum son 3 örneğin en az 2'sinde
  görülürse bir **olay** açılır. Tek bir anlık sıçrama olay açmaz.
- **Kanıt kaydı:** Olay açılınca son ~3 dakikanın kaydı ("olay öncesi") diske yazılır. Olay boyunca her örnek kaydedilir.
- **Kapanış:** 60 saniye boyunca eşiği aşan bekleme kalmazsa olay "düzeliyor" aşamasına geçer. 2 dakika daha
  kaydedilir, sonra kapanır.
- **Normal günler:** Sorun yokken yalnızca dakikada bir küçük bir özet satırı yazılır.

### Önem seviyeleri

Sayılar yalnızca eşiği aşan beklemeleri kapsar. Kısa, normal lock beklemeleri seviyeyi etkilemez.

| Seviye | Ne zaman |
|---|---|
| **Uyarı** | 30 sn'yi aşan en az bir lock beklemesi var |
| **Yüksek** | Eşiği aşan bekleyen sayısı ≥ 5, ya da bir bekleme 2 dakikayı geçti |
| **Kritik** | Eşiği aşan bekleyen sayısı ≥ 20, ya da bekleyenler 2 veya daha fazla veritabanında |

Bir olayın seviyesi yalnızca yükselir, olay sürerken düşmez.

---

## 2. Güvenlik: uygulama neyi yapar, neyi asla yapmaz

**Yapar:**

- Senin mevcut PostgreSQL kullanıcınla bağlanır. Yeni kullanıcı ya da yetki istemez.
- Yalnızca okuma sorguları çalıştırır. Uygulamanın çalıştırabileceği SQL'lerin tamamı kodda tek bir dosyadadır
  (`SqlCatalog.cs`). Kullanıcının SQL yazabileceği bir alan yoktur.
- Bağlantıyı `default_transaction_read_only=on` ile açar ve sunucunun bunu onayladığını kontrol eder. Onay gelmezse
  izleme **başlamaz**.
- Her sorgu sunucu tarafında en fazla 3 saniye çalışabilir (`statement_timeout`).
- `pg_stat_activity`'de **`PgMonitor (read-only)`** adıyla görünür. DB ekibi bağlantını tanıyabilir.

**Asla yapmaz:**

- Session sonlandırmaz, sorgu iptal etmez, ayar değiştirmez, istatistik sıfırlamaz.
- Şifreleri dosyaya, loga, veritabanına ya da maile yazmaz. Şifreler yalnızca **Windows Credential Manager**'da
  durur. İstersen hiç kaydetmeden sadece o oturum için de kullanabilirsin.
- PostgreSQL ve senin SMTP sunucun dışında hiçbir yere bağlanmaz. Telemetri, bulut ya da otomatik güncelleme yoktur.

**Bilmen gereken:** Olay kayıtları ve mailler SQL metni içerir; Mendix sorgularında parametre değerleri genelde `$1`
gibi görünür. Kayıt dosyası yalnızca senin Windows kullanıcının klasöründedir. Mailde SQL istemiyorsan
[6. bölümdeki](#6-e-posta-ayarları) "Mailde SQL" değerini `0` yap.

---

## 3. Kurulum

### A. Kaynak koddan çalıştırma (.NET 10 SDK kuruluysa)

```
git clone https://github.com/onurbass/PostgreMonitoringTool.git
cd PostgreMonitoringTool
dotnet run --project src/PgMonitor.App
```

### B. Kurulumsuz paket hazırlama (şirket laptop'u için önerilen)

.NET kurulu olan bir bilgisayarda bir kez şunu çalıştır:

```
dotnet publish src/PgMonitor.App -c Release -r win-x64 --self-contained -o publish
```

- Ortaya çıkan `publish` klasörü (yaklaşık 180 MB) kendi başına çalışır. Hedef bilgisayarda .NET kurulu olması gerekmez.
- Klasörü şirket laptop'una kopyala ve `PgMonitor.exe`'yi çalıştır.
- Yönetici yetkisi gerekmez.

### Şirket ortamında karşılaşılabilecekler

| Durum | Ne yapmalı |
|---|---|
| Antivirüs veya uygulama denetimi `PgMonitor.exe`'yi engelliyor | Uygulama imzasız ve kendi derlediğin bir programdır. BT'den izin iste. |
| VPN dışındayken bağlanamıyor | PostgreSQL ve SMTP sunucusuna ağ erişimi gerekir. |
| Şirket kuralları dışarıdan kod getirmeyi kısıtlıyor | Kopyalamadan önce kurallarını kontrol et. Kodda şirket verisi yoktur. |

---

## 4. İlk açılış: demo modu

Uygulama ilk açılışta **Demo** modundadır:

- Üstte turuncu **SİMÜLASYON VERİSİ** şeridi görünür.
- Ekrandaki her şey kurgusaldır (`app_orchid_db` gibi uydurma veritabanları).
- Hiçbir ağ bağlantısı açılmaz ve gerçek mail gönderilmez.

Demo yaklaşık 10 kat hızlı çalışır, böylece bir dakika içinde bir lock olayı görürsün. Demo saatleri bu yüzden gerçek
saatin ilerisinde görünür. Demonun ürettiği mailler, sağ üstteki **Mail dosyaları** düğmesiyle açılan klasöre `.eml`
dosyası olarak yazılır; Outlook ile açabilirsin.

Farklı durumları görmek için **Ayarlar → Senaryo** listesinden senaryo seçebilirsin:

| Senaryo | Ne gösterir |
|---|---|
| S01 | Sağlıklı bir gün; olay açılmaz |
| S02 | Tek örneklik sıçrama; olay açılmaz |
| S04 | 30 dakika boyunca gelip giden lock'lar; tek olay |
| S05 | `idle in transaction` bir session'ın arkasında 8 session'lık zincir; DB ekibi temizler (en iyi demo) |
| S06 | Kısmi görünürlük (yetki eksikliği) |
| S07 | Olay sırasında bağlantı kopması ve laptop uykusu |
| S08 | PostgreSQL'in yeniden başlaması |

---

## 5. Canlı izlemeye geçiş

**Ayarlar** sekmesinde sırasıyla:

1. **Veri kaynağı:** "Canlı – gerçek PostgreSQL (salt okunur)" seçeneğini işaretle.
2. **PostgreSQL bağlantısı:**
   - **Sunucu**, **Port** (genelde 5432) ve **Kullanıcı** alanlarını doldur.
   - **Veritabanı** olarak bağlanabildiğin herhangi bir veritabanını yaz (örneğin `postgres`). Uygulama buradan
     cluster'daki **tüm** veritabanlarının session'larını görür.
   - **SSL** için şirketinde ne kullanılıyorsa onu seç. Bilmiyorsan `Prefer` ile başla.
3. **Şifre:** Kutuya şifreni yaz, sonra iki düğmeden birine bas:
   - **Şifreyi kaydet**: Windows Credential Manager'a kaydeder; uygulamayı her açtığında tekrar sormaz.
   - **Sadece bu oturum**: hiçbir yere kaydetmez; uygulama kapanınca unutulur.

   Şifre kutusu düğmeye basınca temizlenir. "Şifre kayıtlı." yazısı durumu gösterir. **Şifreyi sil** ile kaydı
   kaldırabilirsin.
4. **Bağlantıyı test et** düğmesine bas. Sonuç sağ alttaki mesaj kutusunda görünür (ayrıntılar aşağıda).
5. **Kaydet ve uygula** düğmesine bas. İzleme hemen başlar. Turuncu şerit kaybolur; üstte "CANLI – kullanıcı@sunucu…"
   yazar.

### Bağlantı testinin sonucu

| Satır | Ne anlama gelir |
|---|---|
| `PostgreSQL 15.x` | Sunucu sürümü |
| `Salt okunur: EVET` | Bağlantı yazma yapamaz. **HAYIR** görürsen uygulama izlemeye başlamaz. |
| `Yetki: superuser / pg_read_all_stats / kısıtlı` | **kısıtlı** ise başka kullanıcıların session ayrıntılarını göremezsin (aşağıya bak). |
| `Görünen session`, `ayrıntısı gizli` | Gizli sayısı 0 değilse görünürlük **KISMİ**dir. |
| `Sorgu süreleri` | İki izleme sorgusunun süresi. Birkaç yüz ms'nin altında olması beklenir. |
| `Sorgu metni sınırı` | PostgreSQL'in sakladığı SQL uzunluğu (genelde 1024 bayt). Daha uzun sorgular kesik görünür. |

**Kısmi görünürlük:** Hesabının `pg_read_all_stats` yetkisi yoksa diğer kullanıcıların session'larında durum, bekleme
ve SQL bilgisi gizli gelir. Gizli session'lardaki lock'lar **görülemez**. Uygulama bu durumu tahmin etmeye çalışmaz;
ekranda açıkça "KISMİ" yazar. Yetki istemek senin kararındır, uygulama hiçbir zaman istemez.

---

## 6. E-posta ayarları

**Ayarlar → E-posta** bölümü:

| Alan | Açıklama |
|---|---|
| Olaylarda e-posta gönder | Ana açma/kapama |
| SMTP sunucu / Port | Şirket mail sunucun (örn. port 587) |
| Güvenlik | `StartTls` (587), `SslOnConnect` (465), `None` (sadece TLS desteklemeyen iç sunucular için) |
| Kullanıcı (ops.) | SMTP kimlik doğrulama istemiyorsa boş bırak |
| Şifre | PostgreSQL şifresindeki gibi: **Şifreyi kaydet** veya **Sadece bu oturum** |
| Gönderen | Örn. `pgmonitor@sirket.com` |
| Alıcılar | Birden fazla adresi `;` ile ayır |
| Mailde SQL (karakter) | Her session için mailde gösterilecek SQL uzunluğu. `0` = mailde SQL yok. Varsayılan 500. |
| Önem arttığında da gönder | Olay büyüyünce ek mail atar (olay başına en fazla 10 dakikada bir) |

Değişikliklerden sonra **Test maili gönder** ile dene, sonra **Kaydet ve uygula**'ya bas.

Hangi durumda mail gider:

| Durum | Mail |
|---|---|
| Olay açıldı | Her zaman |
| Olay yeniden açıldı (kapandıktan sonraki 10 dk içinde tekrar başladı) | Her zaman |
| Önem arttı | Ayar açıksa, en fazla 10 dakikada bir |
| Olay kapandı | Her zaman |
| Normal gün, tek örneklik sıçrama, bağlantı kopması | Gitmez |

SMTP çalışmıyorsa mailler kaybolmaz: önce bir kuyruğa yazılır ve 1, 2, 4 … 60 dakika arayla 24 saat boyunca tekrar
denenir. SMTP sorunu izlemeyi hiçbir zaman durdurmaz. Kuyruk durumu Pano'nun altında "Mail:" satırında görünür.

---

## 7. Eşikler ve uygulama adları

**Eşikler:**

| Alan | Varsayılan | Ne zaman değiştirilir |
|---|---|---|
| Lock bekleme eşiği (sn) | 30 | Çok fazla olay açılıyorsa yükselt (örn. 60). Daha erken haber istiyorsan düşür (en az 5). |
| Yüksek: bekleyen sayısı ≥ | 5 | |
| Kritik: bekleyen sayısı ≥ | 20 | |

İlk gün varsayılanlarla çalıştır. Sonra kaç olay açıldığına bakarak ayarla.

**Veritabanı → uygulama adı:** `application_name` genelde sadece `PostgreSQL JDBC Driver` olduğu için hangi Mendix
uygulamasının hangi veritabanını kullandığını buraya yazabilirsin. Her satıra bir eşleşme yaz:

```
mendix_satis_db=Satış Portalı
mendix_ik_db=İK Uygulaması
```

Ekranlarda ve maillerde `mendix_satis_db (Satış Portalı)` şeklinde görünür.

---

## 8. Pano ekranını okumak

**Durum şeridi:**

| Renk ve yazı | Anlamı |
|---|---|
| Yeşil – SAĞLIKLI | 30 sn'yi aşan lock beklemesi yok |
| Kırmızı – OLAY #n – UYARI / YÜKSEK / KRİTİK | Olay sürüyor |
| Sarı – OLAY #n – DÜZELİYOR | Beklemeler bitti; kapanış öncesi 2 dakika daha kayıt alınıyor |
| Sarı – KISMİ GÖRÜNÜRLÜK | Bazı session'lar yetki nedeniyle gizli |
| Kırmızı – VERİ ALINAMIYOR | Bağlantı yok, zaman aşımı, şifre hatası… Bu süre "düzeldi" sayılmaz |

**Kartlar:**

| Kart | Anlamı |
|---|---|
| Son örnek | Son verinin saati (sunucu saati) |
| Session | Görünen client session sayısı |
| Lock bekleyen | Şu an lock bekleyen session sayısı (kısa beklemeler dahil) |
| Eşiği aşan | 30 sn'yi aşanlar; olayı ve seviyeyi bunlar belirler |
| En uzun bekleme | En eski bekleyenin süresi |
| Görünürlük | Tam / KISMİ |
| Uzun transaction | Eşikten (varsayılan 10 dk) uzun süredir açık transaction sayısı, kimseyi bloklamasa da |

**Üç süre ne demek?** Tablolarda aynı session için üç ayrı süre görürsün:

| Sütun | Hesap | Ne anlatır |
|---|---|---|
| Tx süresi / Transaction yaşı | `now() - xact_start` | Transaction ne kadar süredir açık |
| **Durum süresi** | `now() - state_change` | Session şu anki durumunda ne kadar süredir bekliyor. `idle in transaction` bir session için: uygulamanın transaction'ı açık bırakıp ne kadar süredir hiçbir şey yapmadığı |
| Sorgu süresi / Bekleme süresi | `now() - query_start` | `active` ise şu anki sorgunun süresi; değilse son sorgunun başlangıcından beri geçen süre |

**Kök bloklayıcılar (şüpheli):**

- Lock zincirinin başındaki session'lardır, yani kendisi kimseyi beklemeyen ama başkalarını bekleten session'lar.
- "Bekleyen" sütunu arkasındaki toplam session sayısını, "Derinlik" zincirin kaç kat olduğunu gösterir.
- En sık görülen tablo: **`idle in transaction`** durumunda, transaction'ı dakikalardır açık duran bir session. Uygulama
  bir şeyi güncelleyip transaction'ı kapatmayı unutmuş ya da çok uzun bir iş yapıyordur.
- Satıra tıklayınca SQL'in tamamı alttaki kutuda görünür. Bu SQL, session'ın **son** çalıştırdığı sorgudur; lock'u
  alan sorgu daha önce çalışmış olabilir.
- "Şüpheli" denmesinin sebebi: PostgreSQL zincirin başını söyler, ama asıl neden uygulamanın o transaction'ı neden açık
  tuttuğudur. Bunu kod ya da uygulama logları gösterir.

**Lock bekleyen session'lar:** En uzun bekleyen en üstte. "Bloklayan" sütunu, bu session'ı doğrudan bekleten PID'dir.
`0` görürsen beklenen şey bir *prepared transaction*'dır.

Panonun altındaki sekmeler:

- **Uzun transaction'lar:** Şu an eşikten uzun süredir açık olan transaction'lar. Kimseyi bloklamasalar da listelenirler
  (ayrıntılar [9a](#9a-uzun-transactionlar) bölümünde).
- **Veritabanları:** Her veritabanındaki session, aktif, `idle in transaction` ve lock bekleyen sayıları.
- **Son olaylar:** Olay açıldı, önem arttı, düzelme başladı, veri alınamıyor, sunucu yeniden başladı gibi olaylar. Liste
  yerel kayıttan okunur. Uygulamayı kapatıp açsan da, **Kaydet ve uygula**'ya bassan da kaybolmaz.

---

## 9. Bir olayı sonradan incelemek

DB ekibi session'ları temizledikten sonra:

1. **Olaylar** sekmesini aç. Soldaki listeden olayı seç.
2. **Zaman çizelgesi:** Olay ne zaman açıldı, ne zaman büyüdü, hangi veritabanları eklendi, ne zaman bitti.
3. **Örnekler:** En kötü an (en çok lock bekleyenin olduğu örnek) otomatik seçili gelir. Diğer anlara tıklayarak
   olayın gelişimini izleyebilirsin. "olay öncesi" aşamasındaki örnekler sorun başlamadan önceki ~3 dakikadır.
4. **O andaki kök bloklayıcılar:** O anda zincirin başında kim vardı?
5. **O andaki session'lar:** O anda transaction'ı açık olan ya da çalışan tüm session'lar (boştaki session'lar
   kaydedilmez, çünkü lock tutamazlar). Bir satıra tıklayınca SQL'in tamamı en altta görünür.

**Pratik ipuçları:**

- Kök bloklayıcının **Tx yaşı** sütunu, transaction'ın ne kadar süredir açık olduğunu gösterir. Olay başladığında bu
  süre zaten uzunsa, sorun olaydan önce başlamıştır. "olay öncesi" örneklerine bak.
- Aynı PID ve aynı veritabanı farklı olaylarda tekrar ediyorsa, o uygulamanın belirli bir akışına bak.
- Session olaydan sonra listeden kaybolduysa sonlandırılmış **ya da** kendiliğinden bitmiş olabilir. Uygulama ikisini
  ayırt edemez.
- "Yarıda kaldı" durumu: olay sürerken uygulama kapanmış demektir. O ana kadarki kanıt kayıtlıdır.

### 9a. Uzun transaction'lar

Bir transaction eşikten (varsayılan **10 dakika**, Ayarlar → Eşikler → "Uzun transaction eşiği") uzun süre açık kalırsa,
**kimseyi bloklamasa bile** listelenir ve kaydedilir.

- **Olay açmaz, mail atmaz.** Sadece gösterir ve kaydeder. Lock olayları ayrıca, kendi kurallarıyla çalışmaya devam eder.
- **Ne kaydedilir:** Transaction ilk kez eşiği geçtiğinde bir kayıt oluşur: pid, veritabanı, kullanıcı, istemci, durum,
  bekleme türü ve SQL. Durum veya sorgu değişirse kayıt dakikada en fazla bir kez güncellenir. Bittiğinde son görüldüğü
  an ve nasıl bittiği yazılır:
  - *session kayboldu (bitti veya sonlandırıldı – ayırt edilemez)*: DB ekibi sonlandırmış ya da uygulama bağlantıyı
    kapatmış.
  - *transaction bitti (session devam ediyor)*: uygulama commit veya rollback yapmış.
  - *uygulama kapandı – sonu görülmedi*: PgMonitor o sırada kapatılmış.
- **Nerede:** Panoda canlı liste; geçmişi ana pencerenin **Uzun transaction'lar** sekmesinde (en yeni üstte). Geçmişteki
  süreler, transaction'ın son görüldüğü ana göredir.
- **Bağlantı kopması ya da uyku** bir transaction'ı "bitti" saymaz. Ancak uzun süren bir kesintide bitip yeniden başlayan
  bir transaction görülemez.
- **Ek yük yok.** Bu bilgilerin hepsi zaten okunan `pg_stat_activity` verisinden çıkar.
- Süren bir kayıt dakikada bir güncellenir. Uygulama aniden kapansa bile süre en fazla bir dakika eksik görünür.

### 9b. Lock beklemeleri (kısa olanlar dahil)

Ana penceredeki **Lock beklemeleri** sekmesi, en az **5 saniye** (Ayarlar → Eşikler → "Kaydedilecek en kısa lock
beklemesi") süren **her** lock beklemesini kaydeder. Buna 30 saniye eşiğini aşmadığı için olay açmayan kısa beklemeler de
dahildir. "Panoda bir sorgu göründü, birkaç saniye sonra kayboldu" dediğin durumlar burada kalır.

| Sütun | Anlamı |
|---|---|
| İlk görüldü | Beklemenin ilk görüldüğü örnek |
| En uzun bekleme | Sorgunun başlangıcından, beklerken görüldüğü son ana kadar geçen süre |
| Bloklayan | Doğrudan bekleten PID(ler) (`pg_blocking_pids`) |
| Kök bloklayıcı | Zincirin başı: pid, durum, kullanıcı, istemci, transaction süresi. Altta son SQL'i (şüpheli) |
| Bitiş | *bekleme bitti* (lock alındı ya da sorgu bitti), *session kayboldu* (bitti veya sonlandırıldı), *sonu görülmedi* (PgMonitor o sırada kapandı) |

- Bir satıra tıklayınca altta iki kutu açılır: **bekleyen sorgu** ve **kök bloklayıcının son sorgusu**.
- Olay açmaz, mail atmaz. Prod'a ek sorgu da göndermez; mevcut örneklerden çıkar.
- **Örnekleme aralığından kısa beklemeler görülmeyebilir.** Varsayılan aralık 10 saniye olduğu için 5–10 saniyelik
  bir bekleme bazen iki örneğin arasına düşer.
- Örnek başına en fazla 50 bekleyen için "kim blokluyor" sorulur. Daha fazlası varsa en yenilerin "Bloklayan" sütunu
  boş kalır.

---

## 10. E-postayı okumak

Konu satırı örneği:

```
[PgMonitor] Olay #12 AÇILDI – Yüksek – mendix_satis_db (Satış Portalı) – 8 lock bekleyen
```

Mailin gövdesinde sırayla:

- Olay zamanı, başlangıç (ve kapanışta bitiş ile süre), etkilenen veritabanları, görünürlük.
- **ŞU ANKİ DURUM:** kök bloklayıcılar (pid, veritabanı, kullanıcı, IP, durum, transaction yaşı, bekleyen sayısı, SQL)
  ve en uzun bekleyenler.
- **EN KÖTÜ AN** (kapanış mailinde): olay boyunca en çok session'ın beklediği an.

Demo modunun mailleri `[SİMÜLASYON]` ile başlar ve gerçekte gönderilmez.

---

## 11. Laptop'ta çalıştırmak

- **Pencereyi kapatmak** uygulamayı kapatmaz: sistem tepsisine (saatin yanı, kalkan simgesi) iner ve izleme sürer.
  Simgeye çift tıklayınca pencere geri gelir. Tamamen kapatmak için simgeye sağ tıklayıp
  **Çıkış (izlemeyi durdur)** seç.
- Olay açılınca tepsiden bir bildirim çıkar.
- **Uyku, kapak kapatma, VPN kopması:** Bu sürelerde veri alınamaz. Uygulama bu boşlukları "veri alınamıyor" olarak
  kaydeder ve **asla "düzeldi" saymaz**. Uyanınca olayın kapanması için yeniden temiz örnekler gerekir.
- **Prizdeyken bilgisayarı uyutma** (Ayarlar → Laptop): işaretlersen, uygulama açık ve laptop prize takılıyken
  Windows'tan uyumamasını ister. Windows ayarlarını değiştirmez; uygulama kapanınca etkisi biter. Kapağı kapatmak yine
  uyutur.
- **Windows açılışında otomatik başlatma:** `Win + R` → `shell:startup` yaz → açılan klasöre `PgMonitor.exe` için bir
  kısayol koy.
- Uygulama aynı anda yalnızca bir kez çalışır. İkinci kez açmaya çalışırsan "zaten çalışıyor" uyarısı görürsün.

---

## 12. Veriler nerede, ne kadar yer kaplar

Hepsi `%LOCALAPPDATA%\PgMonitor` klasöründedir. Sağ üstteki **Veri klasörü** düğmesi bu klasörü açar.

| Dosya/klasör | İçerik |
|---|---|
| `settings.json` | Ayarlar (**şifre içermez**) |
| `monitor.db` (+ `-wal`, `-shm`) | Canlı mod kayıtları: dakikalık özetler, olaylar, örnekler, SQL metinleri, mail kuyruğu |
| `monitor-demo.db` | Demo kayıtları; canlı verilerle hiç karışmaz |
| `logs\` | Günlük log dosyaları (14 gün; SQL ve şifre yazılmaz) |
| `mail-drop\` | Demo maillerinin `.eml` kopyaları |

- **Saklama:** Dakikalık özetler 30 gün, kapanmış olaylar 180 gün tutulur. Süresi dolanlar her saat temizlenir.
- **Boyut sınırı** (varsayılan 1024 MB): Aşılırsa önce en eski kapanmış olaylar silinir. Yer hâlâ yetmezse açık
  olayların detayı seyreltilir (her 5 örnekten biri kalır). **Açık olaylar hiçbir zaman silinmez.** Bu durumda Pano'da
  uyarı çıkar.
- Pano'nun altındaki "Depolama:" satırı güncel boyutu gösterir.
- `monitor.db` dosyasını [DB Browser for SQLite](https://sqlitebrowser.org/) gibi bir araçla da açabilirsin. Uygulama
  çalışırken yalnızca okuma için aç.
- **Her şeyi silmek için:** Uygulamadan çık. Ayarlar'daki **Şifreyi sil** düğmeleriyle şifreleri kaldır (ya da Windows
  Kimlik Bilgisi Yöneticisi'nde `PgMonitor:` ile başlayan kayıtları sil). Sonra klasörü sil.

---

## 13. Sorun giderme

| Belirti | Olası neden ve çözüm |
|---|---|
| VERİ ALINAMIYOR – `Disconnected` | Sunucuya ulaşılamıyor: VPN, sunucu adı veya port yanlış. Uygulama tekrar deneme aralığını giderek uzatır (en fazla 60 sn). |
| VERİ ALINAMIYOR – `Timeout` | Ağ çok yavaş ya da sunucu cevap vermiyor. Ağır yük altındaki sunucuda da görülebilir; bu durum kayıtlıdır. |
| VERİ ALINAMIYOR – `AuthFailed` | Şifre veya kullanıcı yanlış. Hesabın kilitlenmemesi için uygulama **tekrar denemez**. Şifreyi yeniden kaydet ve **Kaydet ve uygula**ya bas. |
| VERİ ALINAMIYOR – `PermissionDenied` | Hesabın bu sorgulara izni yok **ya da** sunucu salt okunur modu onaylamadı. Uygulama tekrar denemez. Bağlantı testi hangisi olduğunu gösterir. |
| KISMİ GÖRÜNÜRLÜK | Hesapta `pg_read_all_stats` yok. Bkz. [5. bölüm](#5-canlı-izlemeye-geçiş). |
| Çok fazla olay açılıyor | Lock bekleme eşiğini yükselt (örn. 60 sn). |
| Mail gitmiyor | Pano altındaki "Mail:" satırında son hatayı gör, **Test maili gönder** ile dene. Şirket SMTP'si kimlik doğrulama veya TLS istiyor olabilir. |
| "Kayıt hatası" uyarısı | Disk dolu veya dosya kilitli. İzleme sürer ama kanıt yazılamaz. Diski kontrol et. |
| Uygulama açılmıyor | Zaten tepside çalışıyor olabilir. Logları kontrol et: `%LOCALAPPDATA%\PgMonitor\logs`. |

Loglar yalnızca olay türlerini ve hata tiplerini içerir. SQL, şifre ve sunucu mesajları loglara yazılmaz.

---

## 14. Şirkette ilk kullanım kontrol listesi

| # | Kontrol | Beklenen |
|---|---|---|
| 1 | Şifreyi kaydet, sonra `%LOCALAPPDATA%\PgMonitor` klasöründe şifreyi ara (örn. Windows aramasıyla) | Hiçbir dosyada bulunmaz |
| 2 | **Bağlantıyı test et** | Sürüm 15.x görünür, `Salt okunur: EVET` |
| 3 | Görünürlük | "Tam" mı "KISMİ" mi, not al |
| 4 | Sorgu süreleri | Birkaç yüz ms'nin altında |
| 5 | DB ekibine sor: `pg_stat_activity`'de `PgMonitor (read-only)` görünüyor mu? | Görünür; her zaman tek bağlantı |
| 6 | Bir gün normal çalıştır | Gereksiz olay yoksa eşikler uygun; varsa eşiği yükselt |
| 7 | **Test maili gönder** | Alıcılara ulaşır |
| 8 | VPN'i kısa süre kes, sonra bağla | Pano'da "VERİ ALINAMIYOR", sonra normale döner; sahte "düzeldi" yok |
| 9 | Gerçek bir lock olayından sonra | DB ekibi temizledikten sonra Olaylar sekmesinde kök bloklayıcı ve SQL görünür |

Bir sorun çıkarsa sadece ekrandaki hata türünü ve log satırını paylaş. Sunucu adı, veritabanı adı ve SQL gibi şirket
bilgilerini paylaşmadan önce kontrol et.

---

## 15. Bilinen sınırlar

- **Örnekleme aralığı:** 10 saniyede bir (olayda 5 sn) bakıldığı için birkaç saniyelik lock'lar görülmeyebilir. Bu
  bilinçli bir tercihtir; o kadar kısa lock'lar sorun değildir. 30 sn eşiğiyle bir olay, bekleme başladıktan yaklaşık
  40–50 saniye sonra açılır.
- **50 session sınırı:** 50'den fazla session aynı anda lock beklerse, en yeni bekleyenler için "kim blokluyor" bilgisi
  alınmaz. Bu session'lar listede görünür ama "Bloklayan" sütunları boş kalır.
- **Bekleme süresi bir üst sınırdır:** Sorgunun başladığı andan ölçülür. Sorgu lock'a sonradan takılmışsa gerçek
  bekleme biraz daha kısadır.
- **Kök bloklayıcının SQL'i:** Session'ın son sorgusudur. Lock'u alan sorgu daha önce çalışmış olabilir.
- **Gizli session'lar:** Yetki yüzünden gizli olan session'lardaki lock'lar görünmez.
- **Uyku ve kesinti:** Laptop uyurken veya ağ yokken hiçbir şey kaydedilemez.
- **Subtrans izleme** (`SubtransSLRU` / `SubtransBuffer` beklemeleri, `pg_stat_slru`) bu sürümde yoktur; sonraki
  aşamadadır. Bu beklemeler şimdilik yalnızca olay sırasında kaydedilen session'larda "Bekleme" sütununda görünür.
- **Sunucu ayrımı:** Uygulama bir PostgreSQL yeniden başlamasını veya başka bir node'a geçişi algılar ve kaydeder.
  Ancak birden fazla cluster'ı aynı anda izlemez.
