# Şirket SQL alarmı: lock alarmı için istek metinleri

Bu belge, izleme ekibinin "SQL İzleme (Otomasyon)" formuna girilecek bilgileri ve alarmın doğrulama adımlarını içerir.
Burada sunucu, kullanıcı ya da veritabanı adı yoktur; `<...>` ile gösterilen yerleri kendi bilgilerinle doldur.

## Ne yapacak

İzleme sistemi aşağıdaki SQL'i **dakikada bir** çalıştırır. Sorgu şu soruyu sorar:

> Lock yüzünden birini **30 saniyeden uzun** süredir bekleten ve transaction'ını (dolayısıyla kilitlerini) **120 saniyeden
> uzun** süredir açık tutan bir session var mı? Varsa hangi veritabanında (yani hangi uygulamada)?

- **Sorun yokken sorgu satır döndürmez**, sadece başlıklar gelir.
- Sorun varsa **bloklayan session'ın veritabanı başına bir satır** döner. Veritabanı adı kaynak uygulamayı verir.
- Tek bir sorgu cluster'daki tüm uygulamaları kapsar; uygulama başına ayrı alarm gerekmez.
- Alarm yalnızca haber verir. Hiçbir session'ı sonlandırmaz; uygulamaların kendi 5 dakikalık script'i aynen devam eder.
  Alarm bu script'ten **önce** çalar: bloklayanın transaction'ı 120 saniyeyi geçtikten sonraki ilk kontrolde, yani en geç
  yaklaşık 3 dakikada.

## SQL

Tek seviye vardır (bekleyen ≥ 30 sn, bloklayanın transaction'ı ≥ 120 sn açık):

```sql
SELECT b.datname                                            AS database_name,
       count(DISTINCT b.pid)                                AS blocking_sessions,
       count(DISTINCT w.pid)                                AS waiting_sessions,
       max(extract(epoch FROM now() - b.xact_start))::int   AS max_blocker_tx_seconds,
       max(extract(epoch FROM now() - b.state_change))::int AS max_blocker_state_seconds,
       max(extract(epoch FROM now() - w.query_start))::int  AS max_wait_seconds
FROM pg_stat_activity w
CROSS JOIN LATERAL unnest(pg_blocking_pids(w.pid)) AS bp(pid)
JOIN pg_stat_activity b ON b.pid = bp.pid
WHERE w.wait_event_type = 'Lock'
  AND w.query_start < now() - interval '30 seconds'
  AND b.wait_event_type IS DISTINCT FROM 'Lock'
  AND b.xact_start < now() - interval '120 seconds'
GROUP BY b.datname
```

### Neden "durum süresi" değil "transaction süresi"

Kilitler transaction bitene kadar tutulur. Bu yüzden "120 saniyeden fazla locklayan" ifadesinin veritabanındaki en yakın
karşılığı **transaction süresidir** (`xact_start`).

Durum süresi (`state_change`), session'ın her yeni sorguda sıfırlanır. Sürekli sorgu çalıştıran ama transaction'ını kapatmayan
bir uygulama (ilk ekran görüntüsündeki gibi: transaction 1:43 saat açık, durum süresi 1 sn) durum süresine bakan bir kuralı
**hiç tetiklemez**, oysa başkalarını saatlerce bekletir. Transaction süresi bu durumu yakalar. `idle in transaction` olarak
takılı kalan session'ları da yakalar, çünkü onların transaction süresi her zaman durum süresinden uzundur.

Durum süresi sonuçta yine gösterilir: `max_blocker_state_seconds` transaction süresine yakınsa session **takılı**dır (genelde
`idle in transaction`). Çok kısaysa session **çalışıyor ama transaction'ı uzun**dur.

Sondaki noktalı virgül bilerek konmadı; aracın kabul ediyorsa eklenebilir, fark etmez.

### Sorgu adım adım

1. `w` = **bekleyen**: şu an bir lock'u bekleyen session (`wait_event_type = 'Lock'`) ve en az 30 saniyedir bekliyor.
2. `pg_blocking_pids(w.pid)` her bekleyen için "seni kim blokluyor?" diye sorar; `unnest` bu listeyi satırlara açar.
3. `b` = **bloklayan**: bulunan PID'nin kendi satırı.
4. `b.wait_event_type IS DISTINCT FROM 'Lock'`: bloklayanın kendisi lock beklemesin; böylece zincirin ortasındakiler elenir,
   **zincirin başı** kalır. (`IS DISTINCT FROM` kullanıldı, çünkü lock beklemeyen bir session'ın bekleme türü boş olabilir.)
5. `b.xact_start < now() - 120 s`: bloklayanın transaction'ı **120 saniyeden uzun süredir açık**; kilitlerini en az bu kadar
   süredir tutuyor olabilir.
6. `GROUP BY b.datname`: sonuç, bloklayanın veritabanı başına tek satır.

Sütunlar: `blocking_sessions` (o veritabanındaki farklı bloklayıcı sayısı), `waiting_sessions` (onları **doğrudan**
bekleyen farklı session sayısı), `max_blocker_tx_seconds` (bloklayanın transaction'ının en uzun açık kaldığı süre),
`max_blocker_state_seconds` (bloklayanın aynı durumda en uzun kaldığı süre) ve `max_wait_seconds` (bekleyenin en uzun bekleme
süresi).

## Form alanları

| Alan | Değer |
|---|---|
| İzleme İstek Tipi | SQL İzleme (Otomasyon) |
| Alarm Adı | `PostgreSQL Lock: takılı session başkasını bekletiyor` |
| Alarm Kritiklik Seviyesi | Test aşamasında: SCC'ye gitmeyen düşük seviye. Canlıya alırken **Critical** |
| Veritabanı Tipi | PostgreSQL |
| Veritabanı Bağlantı Url'i | `jdbc:postgresql://<sunucu>:<port>/<veritabanı>` (cluster'daki herhangi bir veritabanı; `pg_stat_activity` hepsini gösterir) |
| SQL | Yukarıdaki SQL |
| Veritabanı read-only user | İzleme ekibinin servis hesabı. **`pg_monitor` (ya da `pg_read_all_stats`) rolü gerekir**, aksi halde başka kullanıcıların session'ları gizli gelir ve alarm sessizce boş döner |
| Alarm Koşulu | `blocking_sessions > 0` (sorgu satır döndürmüyorsa "alarm yok" sayılmalı; bkz. sorular) |
| Çalışma Sıklığı | 1 dk |
| Çalışma Aralığı | 7/24 |

### İstek açıklaması (öneri)

> Ortak PostgreSQL cluster'ında bir session, başka session'ları 30 saniyeden uzun süredir lock ile bekletiyor ve kendi
> transaction'ını 2 dakikadan uzun süredir açık tutuyor (takılı kalmış ya da kapatılmamış transaction). Sorgu, bloklayan
> session'ın veritabanını döndürür; veritabanı adı kaynak uygulamayı gösterir. Amaç, uygulama ekipleri haber vermeden önce
> lock kaynağı olan uygulamayı bulmak ve ilgili ekibi bilgilendirmektir.

### Alarm mesajı (öneri)

> `<database_name>` veritabanında `<waiting_sessions>` session, en uzun `<max_wait_seconds>` sn'dir lock bekliyor.
> Bekleten `<blocking_sessions>` session'ın transaction'ı `<max_blocker_tx_seconds>` sn'dir açık, `<max_blocker_state_seconds>` sn'dir aynı
> durumda (kök neden için şüpheli).

### Instruction – Event Management System (SCC için, öneri)

> 1. Mesajdaki veritabanı adından uygulamayı ve sorumlu ekibi belirleyin.
> 2. İlgili uygulama ekibine ve DB ekibine "bu veritabanında bir session başkalarını bekletiyor" diye bildirin.
> 3. Session sonlandırma kararı uygulama ve DB ekibine aittir; SCC müdahale etmez.
> 4. Alarm art arda sürüyorsa üst seviyeye yükseltin.

## Test aşaması (önce kendi ekibine)

1. Alarmı düşük seviyede, yalnızca kendi ekibine bildirim gidecek şekilde tanımlat.
2. Aşağıdaki "Doğrulama" adımlarını uygula.
3. Birkaç gün normal çalıştır. Bu sürede şunlara bak:
   - **Hiç alarm gelmiyorsa:** normaldir. Yetki sorununu (aşağıda 1. adım) elemek için doğrulama testini yap.
   - **Gereksiz alarm geliyorsa:** hangi bloklayan durumunda geldiğine bak (`idle in transaction` mı, `active` mi). Uzun çalışan
     meşru bir toplu işlem (`active`) alarmı tetikliyorsa, sadece `idle in transaction` olanlara daralt: `b.xact_start`
     satırının altına `AND b.state = 'idle in transaction'` ekle. Bu, sürekli sorgu çalıştıran uzun transaction'ları
     (ilk ekran görüntüsündeki türü) artık yakalamaz; hangisini istediğine sen karar ver.
   - **Anlık, kendiliğinden geçen takılmalar alarm üretiyorsa:** izleme ekibinden "art arda 2 kontrolde" koşulunu iste.
4. Memnun kalınca seviyeyi **Critical** yap.

## İzleme ekibine sorulacaklar

1. Sorgu satır döndürmezse (sorun yokken) bu "alarm yok" mu sayılıyor, yoksa hata mı oluyor? Hata oluyorsa, sorguyu her zaman
   tek satır dönecek biçimde yeniden yazarım.
2. Koşul bir satır sayısına mı, bir sütun değerine mi bağlanıyor? Sütun adıyla (`blocking_sessions > 0`) verilebiliyor mu?
3. Mesaja sorgu sonucundaki sütunlar (`database_name`, süreler) eklenebiliyor mu? Veritabanı başına satır olduğu için alarm
   **satır bazında** mı tetikleniyor?
4. "Art arda N kontrolde koşul sağlanırsa alarm" tanımlanabiliyor mu? (Aşağıdaki "senaryolardan çıkanlar"a bakın.)
5. DB ekibi servis hesabına `pg_monitor` rolünü verecek mi?

## Senaryolardan çıkanlar (dakikada bir kontrol, demo verisiyle)

| Senaryo | Sonuç |
|---|---|
| Sağlıklı gün (24 saat) | Hiç çalmaz |
| 10 dk süren zincir (idle in transaction kök, 3 doğrudan + 5 dolaylı bekleyen) | 6. dakikada çalar (bloklayanın transaction'ı o an 180 sn açık, 149 sn'dir hareketsiz; 5 dakikalık script'ten önce); temizlikten sonra susar |
| Sürekli sorgu çalıştıran 10 dk'lık transaction (durum süresi 1 sn) bir session'ı bekletiyor | Çalar; durum süresine bakan bir kural burada hiç çalmazdı |
| Tek örneklik 40 sn'lik takılma | **Bir kez** çalar |
| 30 dk boyunca gelip giden kısa lock'lar | Yalnızca **birbirinden ayrı** dört dakikada çalar. "Art arda 2 kontrol" koşulu bunların hepsini eler |
| Başka kullanıcının gizli session'ları | Alarm **sessiz kalır**; yalnızca görünen zincir bulunur |

Bu yüzden iki şey önemli: **art arda kontrol koşulu** (varsa kullanın) ve **servis hesabında yetki**.

Uygulamanın **Alarm önizleme** sekmesi bu kuralı demo ya da canlı örnek üzerinde gösterir; veritabanına ek sorgu göndermez.

## Doğrulama

Sorgunun ilk sürümü şirketteki PostgreSQL 15'te hata vermeden çalıştı; sorun yokken başlıklarla boş döndü. O sürümde bekleyen
süresi şartı, `max_blocker_tx_seconds` ve `max_wait_seconds` sütunları yoktu ve bloklayan için `xact_start` yerine `state_change`
kullanılıyordu. **Bu belgedeki güncel sürüm henüz gerçek bir veritabanında çalıştırılmadı.** Önce bir kez çalıştırıp hata
vermediğini görün. Davranışını denemek için:

### 1. Yetki kontrolü (servis hesabıyla)

```sql
SELECT count(*) AS toplam,
       count(*) FILTER (WHERE state IS NULL) AS gizli
FROM pg_stat_activity
WHERE backend_type = 'client backend' AND pid <> pg_backend_pid();
```

`gizli` sıfırdan büyükse hesap başka kullanıcıların session'larını göremiyor demektir; `pg_monitor` rolü eksiktir.

### 2. Zararsız lock testi (advisory lock)

Hiçbir tabloya ya da gerçek uygulamanın verisine dokunmaz. Rastgele seçilmiş bir sayı üzerinde, yalnızca bu test için bir
kilit oluşturur. **İki pencere de aynı veritabanına bağlı olmalı.** Otomatik commit kapalı bir sorgu penceresi kullan.

**Pencere 1** (bloklayan; pencere açık kalacak):

```sql
BEGIN;
SELECT pg_advisory_xact_lock(7340001);
```

**Pencere 2** (bekleyen; bu sorgu bitmez, bekler):

```sql
SELECT pg_advisory_xact_lock(7340001);
```

Yaklaşık 2,5 dakika sonra **üçüncü bir pencerede** alarm SQL'ini çalıştır: tek satır dönmelidir; `database_name`, bağlandığın
veritabanı olmalıdır. Şirketin alarmı tanımlıysa bir dakika içinde bildirim de gelmelidir.

Testi bitirmek için: Pencere 2'deki sorguyu iptal et, Pencere 1'de `ROLLBACK;` çalıştır.

Dikkat: bu gerçek (zararsız) bir lock bekleyişidir; DB ekibi görebilir. Alarm daha önce devreye alınmışsa tetiklenebilir; testi
başlatmadan önce haber ver. Uygulamaların 5 dakikalık script'i test session'larını da sonlandırabilir; testi 4 dakika içinde bitir.
Bu advisory lock testi henüz gerçek bir PostgreSQL'de denenmedi.

## Bilinen sınırlar

- Dakikada bir kontrol edildiği için bir dakikadan kısa takılmalar görülmez; bu bilinçli bir tercihtir.
- Sorgu yalnızca zincirin başındaki session'ı ve onu **doğrudan** bekleyenleri sayar; zincirin tamamını saymaz.
- Gösterilen veritabanı, **bloklayanın** veritabanıdır. Satır kilitleri aynı veritabanı içinde çalıştığı için bekleyenler de
  genellikle aynı veritabanındadır. Birden fazla uygulamaya yayılan yavaşlama Subtrans'ın işaretidir ve bu alarm onu
  yakalamaz (ayrı bir alarm gerekir: `wait_event IN ('SubtransSLRU','SubtransBuffer')` sayısı).
- Alarm mesajı SQL metni içermez; yalnızca veritabanı ve süreler vardır. Bloklayan sorgunun metni ve PID bilgisi için
  PgMonitor'ün kayıtları kullanılır.
- Alarm "şüpheli kök neden" der: `idle in transaction` bir session'da kilidi alan sorgu, son görünen sorgudan öncekidir.
