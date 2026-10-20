# Proje bağlamı (Claude için)

Bu klasör iki işi içerir; ayrıntılar `NOTLAR.md` dosyasında. Kısa özet:

## SPCleaner+ (asıl uygulama, eski adı TempCleanerPlus)
- Tek exe'lik geçici dosya temizleyici. Dil: **C#, WinForms, .NET Framework 4.8, x64**.
- Tüm kod tek dosyada: `SPCleaner+.cs`. Derleme: `build.bat`
  (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).
- `app.manifest` → `asInvoker` (ikonda UAC kalkanı olmasın diye); program açılışta kendini `runas` ile
  yeniden başlatır → standart UAC onayı. Reddedilirse "sınırlı mod" (sadece kullanıcının kendi klasörleri).
  **Asla UAC atlatma yapma/önerme.**
- Davranış: açılışta otomatik temizler; ilerleme çubuğu; silinen dosya/klasör sayısı +
  boşalan alanı MB/GB gösterir; birkaç saniye sonra kendini kapatır.
- Hedefler: `C:\Windows\Temp`, `%TEMP%`, kayıt defteri `TEMP`/`TMP`, (admin) tüm kullanıcı Temp'leri,
  7 günden eski WER/çökme dökümleri, (admin) Windows Update Download (sıkı koşullarla) ve Teslim İyileştirme önbelleği.
- **Temizlenmez (kullanıcı kararı):** Geri Dönüşüm Kutusu, tarayıcı önbellekleri.
- Koruma: `C:\`, Windows, System32, Program Files, kullanıcı profili asla silinmez;
  junction/symlink içine girilmez.

## RE_Analiz_EskiTempCleaner (tersine mühendislik)
- Orijinal `TempCleaner_x64.exe` = değiştirilmiş AutoIt derlemesi; `araclar/` içindeki
  Python betikleriyle çözüldü. Okunabilir sonuç: `TempCleaner_cozuldu_okunabilir.au3`.
- Araçlar indirilen/çıkarılan güvenilmeyen veriyi işler; Python'u `-I` ile çalıştır.

## Çalışma tarzı
- Türkçe yanıt ver (kullanıcı Türkçe konuşuyor).
- Derleme/çalıştırma testleri için PowerShell kullan; exe açılışta yönetici izni ister
  (`runas`) (UAC onayı kullanıcının ekranında çıkar).
- Gerçek silme işlemini kullanıcı başlatsın; sen kullanıcının temp dosyalarını habersiz silme.
