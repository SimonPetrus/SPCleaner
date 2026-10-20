# SPCleaner+ — Proje Notları

Son güncelleme: 2026-10-20

## Bu klasör ne?
İki iş bir arada:
1. **SPCleaner+** (eski adı TempCleaner+) — sıfırdan yazdığımız, tek exe'lik geçici dosya temizleyici (orijinal `TempCleaner_x64.exe`'nin geliştirilmiş hali).
2. **RE_Analiz_EskiTempCleaner/** — orijinal `TempCleaner_x64.exe`'yi Ghidra + REA ile tersine mühendislikle çözdüğümüz dosyalar.

---

## 1) SPCleaner+ (asıl uygulamamız)

### Dosyalar
- `SPCleaner+.exe` — çalışan uygulama, ürün adı "SPCleaner+", sürüm 2.2.1.0 (~64 KB, ikon gömülü, .NET Framework 4.8, x64)
- `SPCleaner+.cs` — tüm kaynak kod (tek dosya)
- **Tek exe:** ayar dosyası (ini) yok, ayarlar exe içinde sabit; sadece komut satırı parametreleri değiştirir.
- `app.manifest` — `asInvoker` → ikonda UAC kalkanı yok. Program açılışta kendini `runas` ile yeniden
  başlatır, standart UAC onayı yine çıkar (UAC atlatma DEĞİL). "Hayır" denirse **sınırlı mod**: sadece
  kullanıcının kendi Temp + hata raporu/CrashDumps klasörleri; başlıkta "(sinirli mod)" yazar.
  UAC kapalıysa / "Asla bildirme" ayarındaysa soru çıkmadan tam yetkiyle çalışır (Windows'un kendi davranışı).
- `build.bat` — yeniden derleme betiği (ikonu `/win32icon` ile gömer; `/codepage:65001` → kaynakta Türkçe karakter)
- `SPCleaner+.ico` — uygulama ikonu (Windows 11 tarzı: büyük mavi **SP** diskin üstünde, önde süpürge, yanında ışıltılar; 16–256 px)
- `ikon/IkonOlustur.cs` + `ikon/ikon_olustur.bat` — ikonu kodla çizen üreteç. Değiştirmek için `.cs`'i düzenle,
  `ikon_olustur.bat` → `build.bat`. 16–24 px boyutlarda sadece SP harfleri çizilir (detay okunmaz).
  Üreteç çizimin sınırını ölçüp ikon karesini kenardan kenara doldurur (masaüstünde diğer ikonlar kadar büyük görünsün).

### Yeniden derleme
`build.bat`'e çift tıkla (ya da bir komut isteminden çalıştır). Derleyici:
`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (Windows'ta hazır gelir).

### Ne yapıyor
- Açılır açılmaz **otomatik** temizler, kullanıcıya sormaz.
- Temizlediği yerler: `C:\Windows\Temp`, `%TEMP%`, kayıt defterindeki `TEMP`/`TMP`,
  ve yönetici modunda tüm kullanıcıların `...\AppData\Local\Temp` klasörleri.
- Ayrıca **7 günden eski** hata raporları ve çökme dökümleri (otomatik):
  `ProgramData\Microsoft\Windows\WER\ReportQueue|ReportArchive|Temp`, `C:\Windows\Minidump`,
  `C:\Windows\MEMORY.DMP`, her kullanıcının `AppData\Local\Microsoft\Windows\WER\...` ve `AppData\Local\CrashDumps`.
- **Windows Update önbelleği** (`SoftwareDistribution\Download`, v2.2): en risksiz kurallar — sadece yönetici; wuauserv/BITS/
  TrustedInstaller'dan biri çalışıyorsa veya RebootRequired/RebootPending varsa **hiç dokunulmaz** (servis durdurulmaz);
  7 günden eski; kontrol silmeden hemen önce tekrarlanır; kullanımdaki dosya zorlanmaz/yeniden başlatmaya bırakılmaz.
  Normal kullanıcı bu klasöre yazamaz (ACL: Users = ReadAndExecute) → sınırlı modda "yönetici izni gerekli" diye atlanır.
- **Teslim İyileştirme önbelleği** (v2.2): klasöre elle dokunulmaz, PowerShell `Delete-DeliveryOptimizationCache -Force`;
  Status'u "Caching" dışında bir iş varsa (aktif indirme) atlanır, silmeden hemen önce tekrar bakılır. 7 gün kuralı yok
  (Windows bu dosyaları zaten birkaç günde siliyor).
- **Ayrıntılar** (v2.2): iş bitince bildirimde düğme; tıklayınca kaynak başına satırlar (silinen/bulunan, atlananın nedeni).
  Açıkken fare üstündeyse kapanmaz, fare çekilince 10 sn sonra kapanır. Taramada hiçbir şey bulunmayan kaynak
  listede gösterilmez (`SourceStat.Empty`); atlananlar ve bulunup silinemeyenler (soluk "0 B") gösterilir. Log'a da "Kaynaklar:" bölümü yazılır.
- **Silinen dosya + klasör sayısı** ve **boşalan alan (MB/GB)** gösterir.
- **Bildirim penceresi (v2.1, Windows 11 tarzı):** sağ altta alttan kayarak açılır, odağı çalmaz; açık/koyu tema ve
  Windows vurgu rengine uyar; Win11 yerel yuvarlak köşe+gölge (DWM); durum simgesi (Segoe Fluent Icons), ince ilerleme
  çubuğu, canlı dosya sayacı, büyük "boşaldı" değeri, "Sınırlı mod" rozeti; bitince ✕ düğmesi, fare üstündeyken kapanmaz.
  Çizim `ToastPainter` sınıfında, durum `ToastState`'te (önizleme resimleri aynı kodla çizilebilir).
- İş bitince **birkaç saniye sonra soluklaşıp kapanır**.
- Kritik klasörlere asla dokunmaz: `C:\`, Windows, System32, Program Files, kullanıcı profili.
- Junction/symlink'lerin içine girmez (yanlış klasör silmeyi önler).
- Silinemeyen (kilitli) dosyaların salt-okunur/gizli/sistem niteliğini kaldırıp tekrar dener;
  olmazsa yeniden başlatmada silinmek üzere işaretler.

### Komut satırı / çıkış kodları
`/S` sessiz · `/PREVIEW` sadece hesapla (silme yok) · `/LOG` log yaz ·
`/AGE:24` 24 saatten eski · `/CURRENTUSER` sadece bu kullanıcı · `/NOREBOOT`.
Çıkış: `0` başarılı · `1` hata · `2` silinecek yoktu · `-1` zaten çalışıyor.

### Geliştirmedeki tasarım kararları
- **Temizlenmiyor (kullanıcı isteği):** Geri Dönüşüm Kutusu, tarayıcı önbellekleri.
- Prefetch, WinSxS, Installer cache gibi riskli yerler bilinçli olarak dışarıda.

### Kararlar (2026-10-20)
- Uygulamanın adı **SPCleaner+** oldu (eski: TempCleaner+).
- Zamanlanmış görev düğmesi ve tema/dil seçeneği: **istenmedi**, yapılmayacak.
- Güncelleme kontrolü (internete bağlanma) ve ekran kartı gölgelendirici önbelleği: **istenmedi** (2026-10-20).
  Disk alanı göstergesi / toplam istatistik: önerildi, kullanıcı henüz seçmedi.
- Küçük resim/simge önbelleği (thumbcache/iconcache): **temizlenmeyecek** (Explorer kilitli tutar, her seferinde yeniden oluşur).
- requireAdministrator → asInvoker + runas'a geçildi (orijinal TempCleaner da asInvoker; ama o hiç yetki istemez).
- İkon: orijinal TempCleaner'dan kopya değil, kendi çizimimiz (B seçeneği seçildi).
- `/EXCLUDE:a;b` parametresi eklendi (eski ini'deki Exclude ayarının yerine).

---

## 2) RE_Analiz_EskiTempCleaner (orijinalin çözümü)

Orijinal `TempCleaner_x64.exe` aslında **değiştirilmiş bir AutoIt derlemesi**:
standart `AU3!EA06` imzası yerine özel baytlar, farklı şifre anahtarları, iki katman gizleme.

### Dosyalar
- `TempCleaner_cozuldu_okunabilir.au3` — **gizlemesi açılmış, okunabilir betik** (asıl sonuç)
- `TempCleaner_cozuldu_ham.au3` — token çözülmüş ama değişken adları gizli hali
- `entry0.bin` / `entry1.bin` — açılmış ham gömülü veriler (betik + string tablosu)
- `entries.pkl` — ara dosya
- `araclar/` — çıkarma betikleri (sırayla: `1_unpack → 2_lz → 3_detok → 4_deob`)

### Yeniden üretmek (orijinal exe Masaüstü'nde dururken)
```
cd araclar
python -I 1_unpack.py "C:\Users\SPetrus\Desktop\TempCleaner_x64.exe" ..\entries.pkl
python -I 2_lz.py . ..\entries.pkl ..
python -I 3_detok.py ..\entry0.bin ..\TempCleaner_cozuldu_ham.au3
python -I 4_deob.py ..\TempCleaner_cozuldu_ham.au3 ..\entry1.bin ..\TempCleaner_cozuldu_okunabilir.au3
```

### Özet bulgu
Orijinal zararsız: Sordum.org'un (yazar "BlueLife") Temp Cleaner v1.3 aracı.
Sadece Temp klasörlerini siliyor; internet/çalıştırma/kayıt-yazma yok.
SHA-256: `68bc0630e20c481856e2e5e6703913278a912f2d288dbca8ba14bbb1eec6b529`

---

## GitHub
- Repo (private): https://github.com/SimonPetrus/SPCleaner — dal `main`.
- Exe repoda değil, **Releases**'ta: v2.0.0, v2.1.0 (yeni bildirim penceresi), v2.2.0 (Ayrıntılar, Windows Update + Teslim İyileştirme önbelleği), v2.2.1 (Ayrıntılar'da boş kaynaklar gizli) → `SPCleaner+.exe`. Yeni sürüm: `build.bat` → `gh release create vX.Y.Z "SPCleaner+.exe"`.
- Dışarıda (`.gitignore`): `_Sohbet_Kaydi/`, `*.exe`, `*.log`. Repo public yapılırsa `RE_Analiz_EskiTempCleaner/` kaldırılmalı.

## Sohbet kaydı / devam etme
- `_Sohbet_Kaydi/sohbet.jsonl` — bu çalışmanın tam konuşma kaydı.
- VS Code'da **bu klasörü açıp** Claude Code'da `/resume` dersen bu oturum listede çıkar
  ve kaldığımız yerden devam edebilirsin (kayıt proje klasörüne de kopyalandı).
