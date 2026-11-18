# Şirket SQL alarmı: lock alarmı için istek metinleri

Bu belge, izleme ekibinin "SQL İzleme (Otomasyon)" formuna girilecek bilgileri ve alarmın doğrulama adımlarını içerir.
Burada sunucu, kullanıcı ya da veritabanı adı yoktur; `<...>` ile gösterilen yerleri kendi bilgilerinle doldur.

## Ne yapacak

İzleme sistemi aşağıdaki SQL'i **dakikada bir** çalıştırır. Sorgu, şu soruyu sorar:

> Lock yüzünden birini bekleten ve kendisi 2 (ya da 3) dakikadan uzun süredir aynı durumda takılı kalmış bir session var mı?
> Varsa hangi veritabanında (yani hangi uygulamada)?

- **Sorun yokken sorgu satır döndürmez**, sadece başlıklar gelir.
- Sorun varsa **bloklayan session'ın veritabanı başına bir satır** döner. Veritabanı adı kaynak uygulamayı verir.
- Tek bir sorgu cluster'daki tüm uygulamaları kapsar; uygulama başına ayrı alarm gerekmez.
- Alarm yalnızca haber verir. Hiçbir session'ı sonlandırmaz; uygulamaların kendi 5 dakikalık script'i aynen devam eder.
  Alarm bu script'ten **önce** çalar (aşağıdaki eşikler bu yüzden 5 dakikanın altında).

## İki alarm: aynı SQL, iki eşik

| | Erken uyarı | Kritik |
|---|---|---|
| Bekleyen en az | 30 sn bekledi | 60 sn bekledi |
| Bloklayan aynı durumda en az | 120 sn | 180 sn |
| Önem | SCC'ye gitmeyen düşük seviye (governance ekibine) | **Critical** (SCC haberdar edilir) |

Eşikler başlangıç önerisidir; ilk hafta izleyip ayarlanır. Uygulamanın **Alarm önizleme** sekmesi iki seviyenin demo ya da
canlı veride ne döndüreceğini gösterir.

### SQL: erken uyarı

```sql
SELECT b.datname                                            AS veritabani,
       count(DISTINCT b.pid)                                AS bloklayan_session,
       count(DISTINCT w.pid)                                AS bekleyen_session,
       max(extract(epoch FROM now() - b.state_change))::int AS en_uzun_durum_sn,
       max(extract(epoch FROM now() - w.query_start))::int  AS en_uzun_bekleme_sn
FROM pg_stat_activity w
CROSS JOIN LATERAL unnest(pg_blocking_pids(w.pid)) AS bp(pid)
JOIN pg_stat_activity b ON b.pid = bp.pid
WHERE w.wait_event_type = 'Lock'
  AND w.query_start < now() - interval '30 seconds'
  AND b.wait_event_type IS DISTINCT FROM 'Lock'
  AND b.state_change < now() - interval '120 seconds'
GROUP BY b.datname
```

### SQL: kritik

Yukarıdakiyle aynıdır; yalnızca iki sayı değişir: `interval '30 seconds'` → `interval '60 seconds'` ve
`interval '120 seconds'` → `interval '180 seconds'`.

## Form alanları

| Alan | Değer |
|---|---|
| İzleme İstek Tipi | SQL İzleme (Otomasyon) |
| Alarm Adı | `PostgreSQL Lock – Kritik: takılı session başkasını bekletiyor` (erken uyarı için: `… – Erken Uyarı`) |
| Alarm Kritiklik Seviyesi | Kritik alarm: **Critical**. Erken uyarı: SCC'ye gitmeyen düşük seviye |
| Veritabanı Tipi | PostgreSQL |
| Veritabanı Bağlantı Url'i | `jdbc:postgresql://<sunucu>:<port>/<veritabanı>` (cluster'daki herhangi bir veritabanı; `pg_stat_activity` hepsini gösterir) |
| SQL | Yukarıdaki SQL (alarm başına bir tane) |
| Veritabanı read-only user | İzleme ekibinin servis hesabı. **`pg_monitor` (ya da `pg_read_all_stats`) rolü gerekir**, aksi halde başka kullanıcıların session'ları gizli gelir ve alarm sessizce boş döner |
| Alarm Koşulu | `bloklayan_session > 0` (sorgu satır döndürmüyorsa "alarm yok" sayılmalı; bkz. sorular) |
| Çalışma Sıklığı | 1 dk |
| Çalışma Aralığı | 7/24 |

### İstek açıklaması (öneri)

> Ortak PostgreSQL cluster'ında bir session, başka session'ları lock ile bekletiyor ve 2 dakikadan uzun süredir aynı
> durumda (genellikle `idle in transaction`) takılı kalmış. Sorgu, bloklayan session'ın veritabanını döndürür; veritabanı
> adı kaynak uygulamayı gösterir. Amaç, uygulama ekipleri haber vermeden önce lock kaynağı olan uygulamayı bulmak ve
> ilgili ekibi bilgilendirmektir.

### Alarm mesajı (öneri)

> `<veritabani>` veritabanında `<bekleyen_session>` session, en uzun `<en_uzun_bekleme_sn>` sn'dir lock bekliyor.
> Bekleten `<bloklayan_session>` session `<en_uzun_durum_sn>` sn'dir aynı durumda takılı (kök neden için şüpheli).

### Instruction – Event Management System (SCC için, öneri)

> 1. Mesajdaki veritabanı adından uygulamayı ve sorumlu ekibi belirleyin.
> 2. İlgili uygulama ekibine ve DB ekibine "bu veritabanında bir session başkalarını bekletiyor" diye bildirin.
> 3. Session sonlandırma kararı uygulama ve DB ekibine aittir; SCC müdahale etmez.
> 4. Alarm art arda sürüyorsa üst seviyeye yükseltin.

## İzleme ekibine sorulacaklar

1. Sorgu satır döndürmezse (sorun yokken) bu "alarm yok" mu sayılıyor, yoksa hata mı oluyor? Hata oluyorsa, sorguyu her zaman
   tek satır dönecek biçimde yeniden yazarım.
2. Koşul bir satır sayısına mı, bir sütun değerine mi bağlanıyor? Sütun adıyla (`bloklayan_session > 0`) verilebiliyor mu?
3. Mesaja sorgu sonucundaki sütunlar (`veritabani`, süreler) eklenebiliyor mu? Veritabanı başına satır olduğu için alarm
   **satır bazında** mı tetikleniyor?
4. "Art arda N kontrolde koşul sağlanırsa alarm" tanımlanabiliyor mu? (Aşağıdaki "senaryolardan çıkanlar"a bakın.)
5. DB ekibi servis hesabına `pg_monitor` rolünü verecek mi?

## Senaryolardan çıkanlar (dakikada bir kontrol, demo verisiyle)

| Senaryo | Sonuç |
|---|---|
| Sağlıklı gün (24 saat) | İki seviye de hiç çalmaz |
| 10 dk süren zincir (idle in transaction kök, 3 doğrudan + 5 dolaylı bekleyen) | Erken uyarı 6. dakikada, kritik 7. dakikada çalar; temizlikten sonra susar |
| Tek örneklik 40 sn'lik takılma | Erken uyarı **bir kez** çalar, kritik çalmaz |
| 30 dk boyunca gelip giden kısa lock'lar | Erken uyarı yalnızca **birbirinden ayrı** dört dakikada çalar, kritik hiç çalmaz. "Art arda 2 kontrol" koşulu bunların hepsini eler |
| Başka kullanıcının gizli session'ları | Alarm **sessiz kalır**; yalnızca görünen zincir bulunur |

Bu yüzden iki şey önemli: **art arda kontrol koşulu** (varsa kullanın) ve **servis hesabında yetki**.

## Doğrulama

Sorgunun ilk sürümü (bekleyen süresi şartı ve `en_uzun_bekleme_sn` sütunu olmadan) şirketteki PostgreSQL 15'te hata vermeden
çalıştı; sorun yokken başlıklarla boş döndü. **Bu belgedeki güncel sürüm, bu iki eklemeyle birlikte henüz gerçek bir veritabanında
çalıştırılmadı.** Önce bir kez çalıştırıp hata vermediğini görün. Davranışını denemek için:

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

2,5–3 dakika sonra **üçüncü bir pencerede** alarm SQL'ini çalıştır:
- Erken uyarı sorgusu (120/30 sn): yaklaşık 2 dakika sonra tek satır döner; `veritabani` bağlandığın veritabanı olmalıdır.
- Kritik sorgu (180/60 sn): yaklaşık 3 dakika sonra satır döner.

Testi bitirmek için: Pencere 2'deki sorguyu iptal et, Pencere 1'de `ROLLBACK;` çalıştır.

Dikkat: bu gerçek bir (zararsız) lock bekleyişidir; DB ekibi görebilir. Alarm daha önce devreye alınmışsa tetiklenebilir; testi
başlatmadan önce haber ver. Uygulamaların 5 dakikalık script'i test session'larını da sonlandırabilir; testi 4 dakika içinde bitir.

## Bilinen sınırlar

- Dakikada bir kontrol edildiği için bir dakikadan kısa takılmalar görülmez; bu bilinçli bir tercihtir.
- Sorgu yalnızca zincirin başındaki session'ı ve onu **doğrudan** bekleyenleri sayar; zincirin tamamını saymaz.
- Gösterilen veritabanı, **bloklayanın** veritabanıdır. Satır kilitleri aynı veritabanı içinde çalıştığı için bekleyenler de
  genellikle aynı veritabanındadır. Birden fazla uygulamaya yayılan yavaşlama Subtrans'ın işaretidir ve bu alarm onu
  yakalamaz (ayrı bir alarm gerekir: `wait_event IN ('SubtransSLRU','SubtransBuffer')` sayısı).
- Alarm mesajı SQL metni içermez; yalnızca veritabanı ve süreler vardır. Bloklayan sorgunun metni ve PID bilgisi için
  PgMonitor'ün kayıtları kullanılır.
- Alarm "şüpheli kök neden" der: `idle in transaction` bir session'da kilidi alan sorgu, son görünen sorgudan öncekidir.
