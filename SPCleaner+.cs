// SPCleaner+ - Gecici (Temp) klasorlerini, eski hata raporlarini, cokme dokumlerini, Windows Update ve
// Teslim Iyilestirme onbelleklerini temizleyen tek-exe arac.
// Derleme: build.bat (bkz. ayni klasor)
// Kullanim (ayar dosyasi yok, ayarlar exe icinde sabit):
//   Cift tik      : Pencereli, otomatik temizler, bitince kapanir (UAC onayi ister)
//   /S            : Sessiz (penceresiz)            /PREVIEW     : Silmez, sadece hesaplar
//   /LOG          : Exe yanina log yazar           /AGE:24      : 24 saatten eski dosyalar
//   /CURRENTUSER  : Sadece su anki kullanici       /NOREBOOT    : Kilitli dosyalari yeniden baslatmaya birakma
//   /EXCLUDE:a;b  : Haric tutulacak desenler (* ve ? kullanilabilir)
//   Yonetici izni : Acilista UAC onayi istenir (kendini "runas" ile yeniden baslatir). Reddedilirse
//                   sadece kullanicinin kendi Temp/hata raporu klasorleri temizlenir ("sinirli mod").
//   Windows Update: Download klasoru; sadece wuauserv/BITS/TrustedInstaller durmusken ve yeniden baslatma
//                   beklenmiyorken, 7 gunden eski dosyalar; kontrol silmeden hemen once tekrarlanir.
//   Teslim Iyil.  : Windows'un Delete-DeliveryOptimizationCache komutuyla; aktif indirme varsa atlanir.
//   Cikis kodlari : 0=basarili  1=hata oldu  2=silinecek sey yoktu  -1=zaten calisiyor
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SPCleaner+")]
[assembly: AssemblyDescription("SPCleaner+ - Gecici dosya temizleyici")]
[assembly: AssemblyProduct("SPCleaner+")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]

namespace SPCleanerPlus
{
    // ---------------------- Ayarlar ----------------------
    // Ayarlar exe icinde sabittir (ini dosyasi yok). Sadece komut satiri parametreleri degistirir.
    internal class Settings
    {
        public bool Silent = false;        // /S  -> pencere gosterme
        public bool Preview = false;       // /PREVIEW -> hicbir sey silme, sadece ne silinecegini goster
        public bool Log = false;           // /LOG -> exe yaninda log dosyasi
        public bool AllUsers = true;       // yonetici modunda tum kullanicilarin Temp klasoru
        public bool ScheduleReboot = true; // kilitli dosyalari yeniden baslatmada sil
        public int AgeHours = 0;           // /AGE:N -> N saatten eski dosyalar (0 = hepsi)
        public int AutoCloseSeconds = 4;   // is bitince kac saniye sonra kapansin
        public List<string> Excludes = new List<string>(); // /EXCLUDE: haric tutulacak desenler
        public bool Elevated = false;      // /ELEVATED -> yetkisi yukseltilmis kopya (dahili, dongu olmasin diye)

        // Hata raporlari (WER) ve cokme dokumleri: sadece bundan eski olanlar silinir
        public const int CrashDataAgeHours = 7 * 24;

        public static Settings Load(string[] args)
        {
            var s = new Settings();
            foreach (var a in args)
            {
                string u = a.Trim();
                string ul = u.ToUpperInvariant();
                if (ul == "/S" || ul == "-S" || ul == "/SILENT") s.Silent = true;
                else if (ul == "/PREVIEW" || ul == "/P") s.Preview = true;
                else if (ul == "/LOG") s.Log = true;
                else if (ul == "/CURRENTUSER" || ul == "/NOALLUSERS") s.AllUsers = false;
                else if (ul == "/NOREBOOT") s.ScheduleReboot = false;
                else if (ul == "/ELEVATED") s.Elevated = true;
                else if (ul.StartsWith("/AGE:")) { int n; if (int.TryParse(u.Substring(5), out n)) s.AgeHours = Math.Max(0, n); }
                else if (ul.StartsWith("/EXCLUDE:")) s.Excludes.AddRange(u.Substring(9).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            }
            return s;
        }

        public static string AppDir()
        {
            return Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory;
        }
    }

    // ---------------------- Kaynak (Ayrintilar listesindeki bir satir) ----------------------
    internal class SourceStat
    {
        public string Label;
        public long Found;        // taramada bulunan boyut
        public long Freed;        // silinen boyut
        public string Skip;       // atlandiysa nedeni (or. "yönetici izni gerekli")
    }

    // ---------------------- Hedef ----------------------
    internal class Target
    {
        public string Path;
        public bool IsFile;        // klasor yerine tek dosya (or. MEMORY.DMP)
        public int MinAgeHours;    // bundan yeni dosyalara dokunma (0 = hepsi)
        public SourceStat Source;  // hangi kaynaga sayilir
        public bool NoReboot;      // kullanimdaki dosya yeniden baslatmaya birakilmaz, sessizce atlanir
        public Func<string> Guard; // silmeden hemen once tekrar sorulur; null degilse hedef atlanir (neden)
        public bool Skipped;

        public override string ToString()
        {
            return Path + (MinAgeHours > 0 ? "  (" + (MinAgeHours / 24) + " gunden eski)" : "");
        }
    }

    // ---------------------- Temizlik motoru ----------------------
    internal class CleanEngine
    {
        private readonly Settings _s;
        private readonly List<Regex> _excludes;
        private readonly HashSet<string> _protected;
        private readonly bool _admin;

        // kaynaklar (Ayrintilar'daki sirayla)
        private readonly SourceStat _srcWinTemp = new SourceStat { Label = "Windows Temp" };
        private readonly SourceStat _srcUserTemp = new SourceStat { Label = "Kullanıcı Temp" };
        private readonly SourceStat _srcDelivery = new SourceStat { Label = "Teslim İyileştirme önbelleği" };
        private readonly SourceStat _srcUpdate = new SourceStat { Label = "Windows Update önbelleği (7 günden eski)" };
        private readonly SourceStat _srcWer = new SourceStat { Label = "Hata raporları (7 günden eski)" };
        private readonly SourceStat _srcDumps = new SourceStat { Label = "Çökme dökümleri (7 günden eski)" };
        public List<SourceStat> Sources { get { return new List<SourceStat> { _srcWinTemp, _srcUserTemp, _srcDelivery, _srcUpdate, _srcWer, _srcDumps }; } }

        // bulunan ogeler
        private readonly List<string> _files = new List<string>();
        private readonly List<long> _fileSizes = new List<long>();
        private readonly List<Target> _fileTargets = new List<Target>();
        private readonly List<KeyValuePair<string, Target>> _dirs = new List<KeyValuePair<string, Target>>();    // normal alt klasorler (en derin once silinir)
        private readonly List<KeyValuePair<string, Target>> _reparse = new List<KeyValuePair<string, Target>>(); // junction/symlink -> sadece baglanti silinir, icine girilmez

        public long TotalBytes { get; private set; }
        public int TotalFiles { get { return _files.Count; } }

        public int FilesDeleted { get; private set; }
        public int FoldersDeleted { get; private set; }
        public long BytesFreed { get; private set; }
        public int ScheduledForReboot { get; private set; }
        public int Errors { get; private set; }

        private readonly List<string> _logLines = new List<string>();

        public CleanEngine(Settings s)
        {
            _s = s;
            _excludes = new List<Regex>();
            foreach (var p in s.Excludes)
            {
                string pat = p.Trim();
                if (pat.Length == 0) continue;
                string rx = "^.*" + Regex.Escape(pat).Replace("\\*", ".*").Replace("\\?", ".") + ".*$";
                try { _excludes.Add(new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); } catch { }
            }
            _protected = BuildProtectedSet();
            _admin = IsAdmin();
        }

        // ---- hedef klasorleri bul ----
        public List<Target> BuildTargets()
        {
            var set = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase); // norm -> hedef
            int crashAge = Settings.CrashDataAgeHours;

            Action<string, int, SourceStat> addDir = (p, age, src) =>
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try
                {
                    string full = Path.GetFullPath(p.Trim());
                    if (!Directory.Exists(full)) return;
                    try { full = new DirectoryInfo(full).FullName; } catch { }
                    string norm = Norm(full);
                    if (IsProtected(full)) return;      // guvenlik: kritik klasor hedef olamaz
                    if (!set.ContainsKey(norm)) set[norm] = new Target { Path = full, MinAgeHours = age, Source = src };
                }
                catch { }
            };
            Action<string> addTemp = p => addDir(p, 0, _srcUserTemp);

            Action<string, int, SourceStat> addFile = (p, age, src) =>
            {
                try
                {
                    string full = Path.GetFullPath(p);
                    if (!File.Exists(full)) return;
                    string norm = Norm(full);
                    if (!set.ContainsKey(norm)) set[norm] = new Target { Path = full, IsFile = true, MinAgeHours = age, Source = src };
                }
                catch { }
            };

            // kullanici profiline bagli hedefler: hata raporlari + uygulama cokme dokumleri
            Action<string> addUserCrashData = localAppData =>
            {
                if (string.IsNullOrEmpty(localAppData)) return;
                addDir(Path.Combine(localAppData, @"Microsoft\Windows\WER\ReportQueue"), crashAge, _srcWer);
                addDir(Path.Combine(localAppData, @"Microsoft\Windows\WER\ReportArchive"), crashAge, _srcWer);
                addDir(Path.Combine(localAppData, "CrashDumps"), crashAge, _srcDumps);
            };

            const string NeedAdmin = "yönetici izni gerekli";
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (_admin) addDir(Path.Combine(winDir, "Temp"), 0, _srcWinTemp);   // sinirli modda zaten silinemez
            else _srcWinTemp.Skip = NeedAdmin;
            addTemp(Path.GetTempPath());
            addTemp(Environment.GetEnvironmentVariable("TEMP"));
            addTemp(Environment.GetEnvironmentVariable("TMP"));

            // sistem geneli hata raporlari (WER) ve cokme dokumleri (yonetici gerekir)
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (_admin)
            {
                if (!string.IsNullOrEmpty(programData))
                {
                    addDir(Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"), crashAge, _srcWer);
                    addDir(Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"), crashAge, _srcWer);
                    addDir(Path.Combine(programData, @"Microsoft\Windows\WER\Temp"), crashAge, _srcWer);
                }
                addDir(Path.Combine(winDir, "Minidump"), crashAge, _srcDumps);
                addFile(Path.Combine(winDir, "MEMORY.DMP"), crashAge, _srcDumps);
            }
            addUserCrashData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            // Windows Update indirme onbellegi: sadece guncelleme hicbir sey yapmiyorken, 7 gunden eski dosyalar,
            // kullanimdaki dosya zorlanmaz; servisler biz tarafindan durdurulmaz
            if (!_admin) _srcUpdate.Skip = NeedAdmin;
            else
            {
                string busy = WindowsUpdateBusy();
                if (busy != null) _srcUpdate.Skip = busy;
                else
                {
                    addDir(Path.Combine(winDir, @"SoftwareDistribution\Download"), crashAge, _srcUpdate);
                    Target wu;
                    if (set.TryGetValue(Norm(Path.Combine(winDir, @"SoftwareDistribution\Download")), out wu))
                    {
                        wu.NoReboot = true;
                        wu.Guard = WindowsUpdateBusy;
                    }
                }
            }

            // kullanicinin kayit defterindeki TEMP/TMP
            try
            {
                using (var rk = Registry.CurrentUser.OpenSubKey("Environment"))
                {
                    if (rk != null)
                    {
                        addTemp(ExpandEnv(rk.GetValue("TEMP") as string));
                        addTemp(ExpandEnv(rk.GetValue("TMP") as string));
                    }
                }
            }
            catch { }

            // yonetici modunda tum kullanicilar
            if (_s.AllUsers && _admin)
            {
                try
                {
                    string profiles = GetProfilesDirectory();
                    if (Directory.Exists(profiles))
                    {
                        foreach (var dir in SafeGetDirs(profiles))
                        {
                            string leaf = Path.GetFileName(dir);
                            if (leaf.Equals("Public", StringComparison.OrdinalIgnoreCase)) continue;
                            if (leaf.StartsWith("Default", StringComparison.OrdinalIgnoreCase)) continue; // Default, Default User
                            if (leaf.StartsWith("All Users", StringComparison.OrdinalIgnoreCase)) continue;
                            addTemp(Path.Combine(dir, @"AppData\Local\Temp"));
                            addUserCrashData(Path.Combine(dir, @"AppData\Local"));
                        }
                    }
                }
                catch { }
            }

            return set.Values.ToList();
        }

        // ---- tarama: ne silinecek, toplam boyut ----
        public void Scan(List<Target> targets, Func<bool> cancel)
        {
            foreach (var t in targets)
            {
                if (cancel != null && cancel()) return;
                int age = Math.Max(_s.AgeHours, t.MinAgeHours);
                if (t.IsFile) AddFile(t.Path, age, t);
                else Walk(t.Path, age, t, cancel);
            }
            // en derin klasorler once silinsin
            _dirs.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            ScanDeliveryOptimization();
        }

        private void AddFile(string f, int ageHours, Target t)
        {
            if (IsExcluded(f)) return;
            FileInfo fi;
            try { fi = new FileInfo(f); } catch { return; }
            if (ageHours > 0)
            {
                try { if (fi.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-ageHours)) return; }
                catch { }
            }
            long len = 0;
            try { len = fi.Length; } catch { }
            _files.Add(f);
            _fileSizes.Add(len);
            _fileTargets.Add(t);
            TotalBytes += len;
            t.Source.Found += len;
        }

        private void Walk(string dir, int ageHours, Target t, Func<bool> cancel)
        {
            if (cancel != null && cancel()) return;

            foreach (var f in SafeGetFiles(dir)) AddFile(f, ageHours, t);

            foreach (var d in SafeGetDirs(dir))
            {
                if (IsExcluded(d)) continue;
                if (IsProtected(d)) continue;
                bool reparse = false;
                try { reparse = (new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0; }
                catch { }
                if (reparse)
                {
                    // junction/symlink: hedefine girme, sadece baglantiyi kaldiracagiz
                    _reparse.Add(new KeyValuePair<string, Target>(d, t));
                    continue;
                }
                _dirs.Add(new KeyValuePair<string, Target>(d, t));
                Walk(d, ageHours, t, cancel);
            }
        }

        // ---- temizlik ----
        public void Clean(IProgress<int> progress, Func<bool> cancel)
        {
            int done = 0;
            for (int i = 0; i < _files.Count; i++)
            {
                if (cancel != null && cancel()) break;
                string f = _files[i];
                long size = _fileSizes[i];
                Target t = _fileTargets[i];
                done++;
                if (progress != null && (done % 16 == 0 || done == _files.Count)) progress.Report(done);
                if (!Allowed(t)) continue;
                try
                {
                    ClearAttributes(f);
                    File.Delete(f);
                    FilesDeleted++;
                    BytesFreed += size;
                    t.Source.Freed += size;
                }
                catch (Exception ex)
                {
                    if (t.NoReboot) LogErr(f, ex);   // kullanimda: zorlanmaz, hata sayilmaz
                    else if (_s.ScheduleReboot && _admin && TryScheduleDelete(f)) ScheduledForReboot++;
                    else { Errors++; LogErr(f, ex); }
                }
            }
            if (progress != null) progress.Report(_files.Count);

            // junction/symlink baglantilarini kaldir (hedef veriye dokunmaz); once bunlar ki ust klasorleri bosalsin
            foreach (var link in _reparse)
            {
                if (cancel != null && cancel()) break;
                if (!Allowed(link.Value)) continue;
                if (TryDeleteLink(link.Key)) FoldersDeleted++;
            }
            // bos kalan klasorleri sil (en derin once)
            foreach (var d in _dirs)
            {
                if (cancel != null && cancel()) break;
                if (!Allowed(d.Value)) continue;
                if (TryDeleteDir(d.Key)) FoldersDeleted++;
            }

            CleanDeliveryOptimization();
        }

        // hedefin korumasi silmeden hemen once bir kez daha sorulur (or. bu arada Windows Update basladiysa)
        private readonly HashSet<Target> _guarded = new HashSet<Target>();
        private bool Allowed(Target t)
        {
            if (t.Guard != null && _guarded.Add(t))
            {
                string reason = t.Guard();
                if (reason != null) { t.Skipped = true; t.Source.Skip = reason; }
            }
            return !t.Skipped;
        }

        private bool TryDeleteLink(string link)
        {
            try
            {
                Directory.Delete(link, false); // sadece baglanti kaldirilir, hedefin icine girilmez
                return true;
            }
            catch (Exception ex)
            {
                Errors++; LogErr(link, ex);
                return false;
            }
        }

        private bool TryDeleteDir(string d)
        {
            try
            {
                if (!Directory.Exists(d)) return false;
                // yas siniri yuzunden icinde dosya kalan klasor hata degil, oylece birakilir
                if (Directory.EnumerateFileSystemEntries(d).Any()) return false;
                try { var di = new DirectoryInfo(d); di.Attributes = FileAttributes.Directory; } catch { }
                Directory.Delete(d, false); // recursive=false: sadece bosalmis klasor
                return true;
            }
            catch (Exception ex)
            {
                Errors++; LogErr(d, ex);
                return false;
            }
        }

        // ---- Windows Update: herhangi bir is yapiyor mu? ----
        // Servislerden biri calisiyorsa veya kurulum/yeniden baslatma bekleniyorsa onbellege dokunulmaz.
        // Durum okunamazsa da dokunulmaz (en temkinli secenek).
        private static string WindowsUpdateBusy()
        {
            foreach (var name in new[] { "wuauserv", "BITS", "TrustedInstaller" })
            {
                try
                {
                    using (var sc = new System.ServiceProcess.ServiceController(name))
                        if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped) return "güncelleme çalışıyor";
                }
                catch (InvalidOperationException) { }   // servis bu sistemde yok
                catch { return "güncelleme durumu okunamadı"; }
            }
            try
            {
                foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending" })
                    using (var rk = Registry.LocalMachine.OpenSubKey(key))
                        if (rk != null) return "güncelleme yeniden başlatma bekliyor";
            }
            catch { return "güncelleme durumu okunamadı"; }
            return null;
        }

        // ---- Teslim Iyilestirme onbellegi: Windows'un kendi komutuyla (klasore elle dokunulmaz) ----
        private bool _doReady;

        private void ScanDeliveryOptimization()
        {
            if (!_admin) { _srcDelivery.Skip = "yönetici izni gerekli"; return; }
            // aktif indirme varsa (durumu "Caching" disinda bir is) dokunulmaz; cikti: <mesgul is sayisi>|<onbellek bayt>
            string o = RunPowerShell(
                "if (-not (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue)) { 'YOK'; exit }" +
                "$b = @(Get-DeliveryOptimizationStatus | Where-Object { $_.Status -ne 'Caching' }).Count;" +
                "$c = (Get-DeliveryOptimizationPerfSnap).CacheSizeBytes; \"$b|$c\"");
            if (o == null) { _srcDelivery.Skip = "durum okunamadı"; return; }
            if (o == "YOK") { _srcDelivery.Skip = "bu Windows'ta desteklenmiyor"; return; }
            string[] p = o.Split('|');
            int busy; long size;
            if (p.Length != 2 || !int.TryParse(p[0], out busy) || !long.TryParse(p[1], out size)) { _srcDelivery.Skip = "durum okunamadı"; return; }
            if (busy > 0) { _srcDelivery.Skip = "indirme sürüyor"; return; }
            _srcDelivery.Found = size;
            TotalBytes += size;
            _doReady = size > 0;
        }

        private void CleanDeliveryOptimization()
        {
            if (!_doReady) return;
            // silmeden hemen once tekrar: bu arada indirme basladiysa vazgec
            string o = RunPowerShell(
                "if (@(Get-DeliveryOptimizationStatus | Where-Object { $_.Status -ne 'Caching' }).Count -gt 0) { 'MESGUL'; exit }" +
                "$a = (Get-DeliveryOptimizationPerfSnap).CacheSizeBytes; Delete-DeliveryOptimizationCache -Force;" +
                "$b = (Get-DeliveryOptimizationPerfSnap).CacheSizeBytes; \"$a|$b\"");
            if (o == "MESGUL") { _srcDelivery.Skip = "indirme sürüyor"; return; }
            long before, after;
            string[] p = o == null ? new string[0] : o.Split('|');
            if (p.Length == 2 && long.TryParse(p[0], out before) && long.TryParse(p[1], out after))
            {
                long freed = Math.Max(0, before - after);
                _srcDelivery.Freed = freed;
                BytesFreed += freed;
                DeliveryFreed = freed;
            }
            else { Errors++; _logLines.Add("HATA  Teslim Iyilestirme onbellegi temizlenemedi :: " + (o ?? "yanit yok")); }
        }

        public long DeliveryFreed { get; private set; }

        private static string RunPowerShell(string script)
        {
            try
            {
                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                var psi = new ProcessStartInfo(exe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.WriteLine(script);
                    p.StandardInput.Close();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return null; }
                    string o = outTask.Result.Trim();
                    return o.Length == 0 ? null : o.Split('\n').Last().Trim();
                }
            }
            catch { return null; }
        }

        // ---- yardimcilar ----
        private void ClearAttributes(string f)
        {
            try
            {
                var a = File.GetAttributes(f);
                if ((a & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                    File.SetAttributes(f, FileAttributes.Normal);
            }
            catch { }
        }

        private bool TryScheduleDelete(string path)
        {
            try { return MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
            catch { return false; }
        }

        private bool IsExcluded(string path)
        {
            if (_excludes.Count == 0) return false;
            foreach (var r in _excludes) if (r.IsMatch(path)) return true;
            return false;
        }

        private bool IsProtected(string path)
        {
            try
            {
                string n = Norm(Path.GetFullPath(path));
                if (n.Length <= 3) return true;          // surucu koku C:\
                return _protected.Contains(n);
            }
            catch { return true; }
        }

        private HashSet<string> BuildProtectedSet()
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = p => { if (!string.IsNullOrEmpty(p)) { try { h.Add(Norm(Path.GetFullPath(p))); } catch { } } };
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            add(win);
            add(Path.Combine(win, "System32"));
            add(Path.Combine(win, "SysWOW64"));
            add(Environment.GetFolderPath(Environment.SpecialFolder.System));
            add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            try { add(GetProfilesDirectory()); } catch { }
            try { add(Path.GetPathRoot(win)); } catch { }
            return h;
        }

        private static string Norm(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            return p.TrimEnd('\\', '/');
        }

        private static string ExpandEnv(string v)
        {
            return string.IsNullOrEmpty(v) ? v : Environment.ExpandEnvironmentVariables(v);
        }

        private static string GetProfilesDirectory()
        {
            try
            {
                using (var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                {
                    if (rk != null)
                    {
                        string pd = rk.GetValue("ProfilesDirectory") as string;
                        if (!string.IsNullOrEmpty(pd)) return Environment.ExpandEnvironmentVariables(pd);
                    }
                }
            }
            catch { }
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetDirectoryName(up);
        }

        private static IEnumerable<string> SafeGetFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return new string[0]; }
        }
        private static IEnumerable<string> SafeGetDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); } catch { return new string[0]; }
        }

        private void LogErr(string path, Exception ex)
        {
            if (!_s.Log) return;
            _logLines.Add("HATA  " + path + "  ::  " + ex.Message);
        }

        public void WriteLog(List<Target> targets)
        {
            if (!_s.Log) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("SPCleaner+ log - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Mod: " + (_s.Preview ? "ONIZLEME" : "TEMIZLIK") + (_s.AgeHours > 0 ? ("  (>" + _s.AgeHours + "s)") : ""));
                sb.AppendLine("Hedefler:");
                foreach (var t in targets) sb.AppendLine("  " + t);
                sb.AppendLine("Bulunan dosya: " + TotalFiles);
                sb.AppendLine("Bulunan boyut: " + Util.HumanSize(TotalBytes));
                sb.AppendLine("Silinen dosya: " + FilesDeleted);
                sb.AppendLine("Silinen klasor: " + FoldersDeleted);
                sb.AppendLine("Bosalan alan: " + Util.HumanSize(BytesFreed));
                sb.AppendLine("Yeniden baslatmada silinecek: " + ScheduledForReboot);
                sb.AppendLine("Hata: " + Errors);
                sb.AppendLine("Kaynaklar:");
                foreach (var src in Sources)
                    sb.AppendLine("  " + src.Label + ": " + (src.Skip != null ? "atlandi (" + src.Skip + ")"
                        : "bulunan " + Util.HumanSize(src.Found) + ", silinen " + Util.HumanSize(src.Freed)));
                if (_logLines.Count > 0) { sb.AppendLine("---- detay ----"); foreach (var l in _logLines) sb.AppendLine(l); }
                File.AppendAllText(Path.Combine(Settings.AppDir(), "SPCleaner+.log"), sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    }

    internal static class Util
    {
        public static string HumanSize(long bytes)
        {
            double b = bytes;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            string fmt = (i == 0) ? "0" : (b >= 100 ? "0" : "0.0");
            return b.ToString(fmt, CultureInfo.GetCultureInfo("tr-TR")) + " " + u[i];
        }
    }

    // ---------------------- Bildirim penceresi (Windows 11 tarzi) ----------------------
    internal enum ToastPhase { Scanning, Cleaning, Done, AlreadyClean, Preview }

    // Pencerede gosterilen her sey; cizim sadece bu durumdan yapilir (onizleme resmi de ayni kodla cizilir)
    internal class ToastState
    {
        public ToastPhase Phase = ToastPhase.Scanning;
        public string Headline = "Taranıyor…";
        public string Detail = "";
        public string BigValue = "";      // sag ust buyuk deger, or. "121 MB"
        public string BigCaption = "";    // altindaki aciklama, or. "boşaldı"
        public string Footnote = "";      // ek bilgi satiri (yeniden baslatma vb.)
        public double Progress = -1;      // 0..1; -1 = belirsiz (kayan cubuk)
        public float Marquee;             // belirsiz cubugun konumu 0..1
        public bool Limited;              // "Sınırlı mod" rozeti
        public bool CloseHover;
        public List<ToastRow> Rows = new List<ToastRow>();   // "Ayrıntılar": kaynak basina sonuc
        public bool Expanded;             // ayrintilar acik mi
        public bool DetailsHover;
    }

    internal class ToastRow
    {
        public string Label, Value;
        public string Note = "";          // atlandiysa nedeni: satirin altinda kucuk ikinci satir
        public bool Dim;                  // bos / atlanan satir soluk gosterilir
    }

    internal class ToastTheme
    {
        public bool Dark;
        public Color Back, Border, Text, SubText, Track, Accent, OnAccent, Success, OnSuccess, HoverBack, PillBack, PillText;

        // Windows'un acik/koyu tema ayari ve vurgu rengi
        public static ToastTheme FromSystem()
        {
            bool dark = false;
            Color? accent = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) dark = (int)v == 0;
                }
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int) { int a = (int)v; accent = Color.FromArgb(a & 0xFF, (a >> 8) & 0xFF, (a >> 16) & 0xFF); }
                }
            }
            catch { }
            return Create(dark, accent);
        }

        public static ToastTheme Create(bool dark, Color? accent)
        {
            var t = new ToastTheme { Dark = dark };
            if (dark)
            {
                t.Back = Hex(0x2C2C2C); t.Border = Hex(0x424242); t.Text = Hex(0xFFFFFF); t.SubText = Hex(0xC5C5C5);
                t.Track = Hex(0x484848); t.HoverBack = Hex(0x3B3B3B);
                t.Accent = accent.HasValue ? Mix(accent.Value, Color.White, 0.45) : Hex(0x4CC2FF);
                t.OnAccent = Hex(0x000000);
                t.Success = Hex(0x6CCB5F); t.OnSuccess = Hex(0x000000);
                t.PillBack = Hex(0x433519); t.PillText = Hex(0xFCE100);
            }
            else
            {
                t.Back = Hex(0xFBFBFB); t.Border = Hex(0xDEDEDE); t.Text = Hex(0x1B1B1B); t.SubText = Hex(0x5E5E5E);
                t.Track = Hex(0xDADADA); t.HoverBack = Hex(0xEBEBEB);
                t.Accent = accent.HasValue ? Mix(accent.Value, Color.Black, 0.1) : Hex(0x005FB8);
                t.OnAccent = Hex(0xFFFFFF);
                t.Success = Hex(0x0F7B0F); t.OnSuccess = Hex(0xFFFFFF);
                t.PillBack = Hex(0xFFF4CE); t.PillText = Hex(0x9D5D00);
            }
            return t;
        }

        private static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }

        private static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    internal static class ToastPainter
    {
        public const int W = 364, H = 122;   // 96 DPI'da piksel

        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        public static RectangleF CloseRect(float sc) { return new RectangleF((W - 40) * sc, 6 * sc, 32 * sc, 28 * sc); }
        public static RectangleF DetailsRect(float sc) { return new RectangleF((W - 40 - 4 - 104) * sc, 6 * sc, 104 * sc, 28 * sc); }   // ust satir, kapat dugmesinin solu

        private const int RowH = 24, NoteH = 15;

        private static int RowHeight(ToastRow r) { return RowH + (r.Note.Length > 0 ? NoteH : 0); }

        // ayrintilar acikken pencere asagiya dogru uzar (pencere de o kadar yukari kayar)
        public static int HeightFor(ToastState st)
        {
            if (!st.Expanded || st.Rows.Count == 0) return H;
            int h = H + 10 + 6;
            foreach (var r in st.Rows) h += RowHeight(r);
            return h;
        }

        public static void Paint(Graphics g, ToastState st, ToastTheme th, float sc, Image appIcon, bool showClose)
        {
            Func<float, float> S = v => v * sc;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(th.Back);

            bool ok = st.Phase == ToastPhase.Done || st.Phase == ToastPhase.AlreadyClean;
            Color stateColor = ok ? th.Success : th.Accent;

            // ust satir: uygulama ikonu + adi (+ sinirli mod rozeti), sagda kapat
            if (appIcon != null) g.DrawImage(appIcon, S(16), S(12), S(16), S(16));
            Font cap = UiFont("Segoe UI", S(12));
            Text(g, "SPCleaner+", cap, th.SubText, new RectangleF(S(40), S(10), S(200), S(20)), StringAlignment.Near);
            if (st.Limited)
            {
                float capW = g.MeasureString("SPCleaner+", cap, PointF.Empty, StringFormat.GenericTypographic).Width;
                Font pf = UiFont("Segoe UI Semibold", S(11));
                string pt = "Sınırlı mod";
                float pw = g.MeasureString(pt, pf, PointF.Empty, StringFormat.GenericTypographic).Width + S(14);
                var pr = new RectangleF(S(40) + capW + S(8), S(11), pw, S(18));
                using (var path = Round(pr, S(9))) using (var b = new SolidBrush(th.PillBack)) g.FillPath(b, path);
                Text(g, pt, pf, th.PillText, pr, StringAlignment.Center);
            }
            if (showClose)
            {
                var cr = CloseRect(sc);
                if (st.CloseHover) using (var path = Round(cr, S(4))) using (var b = new SolidBrush(th.HoverBack)) g.FillPath(b, path);
                Text(g, "", IconFont(S(10)), st.CloseHover ? th.Text : th.SubText, cr, StringAlignment.Center);
            }

            // durum simgesi: renkli daire icinde Fluent ikonu
            var circle = new RectangleF(S(16), S(42), S(32), S(32));
            using (var b = new SolidBrush(stateColor)) g.FillEllipse(b, circle);
            string glyph = st.Phase == ToastPhase.Scanning ? "" : st.Phase == ToastPhase.Cleaning ? "" : st.Phase == ToastPhase.Preview ? "" : "";
            Text(g, glyph, IconFont(S(15)), ok ? th.OnSuccess : th.OnAccent, circle, StringAlignment.Center);

            // sag: buyuk deger (bosalan / bulunan alan)
            float bigW = 0, capW2 = 0;
            if (st.BigValue.Length > 0)
            {
                Font bf = UiFont("Segoe UI Semibold", S(21));
                bigW = g.MeasureString(st.BigValue, bf, PointF.Empty, StringFormat.GenericTypographic).Width;
                Text(g, st.BigValue, bf, ok ? th.Success : th.Accent, new RectangleF(S(60), S(36), S(W - 76), S(30)), StringAlignment.Far);
                Font cf = UiFont("Segoe UI", S(11.5f));
                capW2 = g.MeasureString(st.BigCaption, cf, PointF.Empty, StringFormat.GenericTypographic).Width;
                Text(g, st.BigCaption, cf, th.SubText, new RectangleF(S(60), S(64), S(W - 76), S(18)), StringAlignment.Far);
            }

            // orta: baslik + ayrinti + not
            Text(g, st.Headline, UiFont("Segoe UI Semibold", S(15)), th.Text, new RectangleF(S(60), S(39), S(W - 76) - bigW - S(10), S(24)), StringAlignment.Near);
            Text(g, st.Detail, UiFont("Segoe UI", S(12)), th.SubText, new RectangleF(S(60), S(63), S(W - 76) - capW2 - S(10), S(20)), StringAlignment.Near);
            bool link = showClose && st.Rows.Count > 0;
            if (st.Footnote.Length > 0)
                Text(g, st.Footnote, UiFont("Segoe UI", S(11.5f)), th.SubText, new RectangleF(S(60), S(81), S(W - 76), S(18)), StringAlignment.Near);
            if (link)
            {
                var lr = DetailsRect(sc);
                if (st.DetailsHover) using (var path = Round(lr, S(4))) using (var b = new SolidBrush(th.HoverBack)) g.FillPath(b, path);
                var chevron = new RectangleF(lr.Right - S(24), lr.Y, S(16), lr.Height);
                Text(g, st.Expanded ? "\uE70E" : "\uE70D", IconFont(S(9)), th.Accent, chevron, StringAlignment.Center);
                Text(g, st.Expanded ? "Gizle" : "Ayrıntılar", UiFont("Segoe UI Semibold", S(12)), th.Accent,
                    new RectangleF(lr.X, lr.Y, lr.Width - S(26), lr.Height), StringAlignment.Far);
            }

            // alt: ince ilerleme cubugu
            var track = new RectangleF(S(16), S(104), S(W - 32), S(4));
            using (var path = Round(track, S(2))) using (var b = new SolidBrush(th.Track)) g.FillPath(b, path);
            RectangleF fill;
            if (st.Progress < 0)
            {
                float segW = track.Width * 0.3f;
                float x = track.X - segW + (track.Width + segW) * st.Marquee;
                fill = RectangleF.Intersect(track, new RectangleF(x, track.Y, segW, track.Height));
            }
            else fill = new RectangleF(track.X, track.Y, track.Width * (float)Math.Max(0, Math.Min(1, st.Progress)), track.Height);
            if (fill.Width > 0.5f)
                using (var path = Round(fill, Math.Min(S(2), fill.Width / 2))) using (var b = new SolidBrush(stateColor)) g.FillPath(b, path);

            // ayrintilar: kaynak basina satir (etiket solda, deger sagda)
            if (st.Expanded && st.Rows.Count > 0)
            {
                float y0 = S(H - 2);
                using (var pen = new Pen(th.Border, Math.Max(1f, S(1)))) g.DrawLine(pen, S(16), y0, S(W - 16), y0);
                Font lf = UiFont("Segoe UI", S(12)), vf = UiFont("Segoe UI Semibold", S(12)), nf = UiFont("Segoe UI", S(11));
                float y = y0 + S(8);
                foreach (var row in st.Rows)
                {
                    var rr = new RectangleF(S(16), y, S(W - 32), S(RowH));
                    Font valFont = row.Dim ? lf : vf;
                    // deger sagda; etiket ancak degere kadar uzanir (gerekirse "…" ile kisalir), ust uste binmez
                    float vw = g.MeasureString(row.Value, valFont, PointF.Empty, StringFormat.GenericTypographic).Width;
                    Text(g, row.Value, valFont, row.Dim ? th.SubText : th.Text, rr, StringAlignment.Far);
                    Text(g, row.Label, lf, row.Dim ? th.SubText : th.Text, new RectangleF(rr.X, rr.Y, Math.Max(0, rr.Width - vw - S(12)), rr.Height), StringAlignment.Near);
                    if (row.Note.Length > 0)
                        Text(g, row.Note, nf, th.SubText, new RectangleF(rr.X, rr.Bottom - S(5), rr.Width, S(NoteH)), StringAlignment.Near);
                    y += S(RowHeight(row));
                }
            }
        }

        private static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h)
        {
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            using (var b = new SolidBrush(c))
            {
                sf.Alignment = h;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                g.DrawString(s, f, b, r, sf);
            }
        }

        private static Font UiFont(string family, float px)
        {
            string key = family + "|" + px.ToString(CultureInfo.InvariantCulture);
            Font f;
            if (!_fonts.TryGetValue(key, out f))
            {
                try { f = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch { f = new Font(FontFamily.GenericSansSerif, px, FontStyle.Regular, GraphicsUnit.Pixel); }
                _fonts[key] = f;
            }
            return f;
        }

        // Windows 11: Segoe Fluent Icons, Windows 10: Segoe MDL2 Assets (ayni kod noktalari)
        private static Font IconFont(float px)
        {
            string fam = "Segoe MDL2 Assets";
            try { using (var ff = new FontFamily("Segoe Fluent Icons")) fam = ff.Name; } catch { }
            return UiFont(fam, px);
        }

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.1f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal class CleanForm : Form
    {
        private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

        private readonly Settings _s;
        private readonly ToastState _st = new ToastState();
        private readonly ToastTheme _th = ToastTheme.FromSystem();
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer();
        private Image _icon;
        private float _sc = 1f;
        private int _homeTop, _bottom;
        private DateTime _start, _closeStart, _closeAt = DateTime.MaxValue;
        private bool _finished, _closing, _hover;

        private CleanEngine _engine;
        private List<Target> _targets;

        public CleanForm(Settings s)
        {
            _s = s;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Text = "SPCleaner+";
            BackColor = _th.Back;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); _icon = Icon.ToBitmap(); } catch { }
            using (var g = CreateGraphics()) _sc = g.DpiX / 96f;

            Size = new Size((int)Math.Round(ToastPainter.W * _sc), (int)Math.Round(ToastPainter.H * _sc));
            var wa = Screen.PrimaryScreen.WorkingArea;
            int m = (int)Math.Round(12 * _sc);
            Left = wa.Right - Width - m;
            _bottom = wa.Bottom - m;
            _homeTop = _bottom - Height;
            Top = _homeTop + (int)Math.Round(16 * _sc);
            Opacity = 0;

            _st.Limited = !CleanEngine.IsAdmin();
            _anim.Interval = 15;
            _anim.Tick += (a, b) => OnAnim();
            Load += async (a, b) => { _start = DateTime.Now; _anim.Start(); await RunAsync(); };
        }

        // odagi calmasin, Alt+Tab'da gorunmesin
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2;  // DWMWCP_ROUND: Windows 11'in kendi yuvarlak kosesi ve golgesi (Windows 10'da etkisiz)
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int border = ColorTranslator.ToWin32(_th.Border);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnPaint(PaintEventArgs e)
        {
            ToastPainter.Paint(e.Graphics, _st, _th, _sc, _icon, _finished);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hover = true;
            bool h = _finished && ToastPainter.CloseRect(_sc).Contains(e.Location);
            bool dh = _finished && _st.Rows.Count > 0 && ToastPainter.DetailsRect(_sc).Contains(e.Location);
            if (h != _st.CloseHover || dh != _st.DetailsHover)
            {
                _st.CloseHover = h; _st.DetailsHover = dh;
                Cursor = h || dh ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            if (_st.CloseHover || _st.DetailsHover) { _st.CloseHover = _st.DetailsHover = false; Cursor = Cursors.Default; Invalidate(); }
            // fare ustundeyken kapanmaz; ayrilinca biraz daha bekler (ayrintilar aciksa 10 sn)
            DelayClose(_st.Expanded ? 10 : 1.5);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_finished || e.Button != MouseButtons.Left) return;
            if (ToastPainter.CloseRect(_sc).Contains(e.Location)) BeginClose();
            else if (_st.Rows.Count > 0 && ToastPainter.DetailsRect(_sc).Contains(e.Location)) ToggleDetails();
        }

        // ayrintilar acilinca pencere yukari dogru uzar (alt kenar yerinde kalir)
        private void ToggleDetails()
        {
            _st.Expanded = !_st.Expanded;
            int h = (int)Math.Round(ToastPainter.HeightFor(_st) * _sc);
            SetBounds(Left, _bottom - h, Width, h);
            _homeTop = _bottom - h;
            DelayClose(_st.Expanded ? 10 : 1.5);
            Invalidate();
        }

        private void DelayClose(double seconds)
        {
            if (!_finished) return;
            var t = DateTime.Now.AddSeconds(seconds);
            if (_closeAt < t) _closeAt = t;
        }

        private void OnAnim()
        {
            var now = DateTime.Now;
            if (_closing)
            {
                double t = (now - _closeStart).TotalMilliseconds / 180.0;
                if (t >= 1) { _anim.Stop(); Close(); }
                else Opacity = 1 - t;
                return;
            }

            // giris: 220 ms'de alttan kayarak belirir
            double k = (now - _start).TotalMilliseconds / 220.0;
            if (k < 1.05)
            {
                double ease = 1 - Math.Pow(1 - Math.Min(1, k), 3);
                Opacity = ease;
                Top = _homeTop + (int)Math.Round((1 - ease) * 16 * _sc);
            }

            bool repaint = false;
            if (_st.Progress < 0)
            {
                _st.Marquee = (float)(((now - _start).TotalMilliseconds % 1500) / 1500.0);
                repaint = true;
            }
            if (_st.Phase == ToastPhase.Scanning && _engine != null)
            {
                string d = Num(_engine.TotalFiles) + " dosya bulundu";
                if (d != _st.Detail) { _st.Detail = d; repaint = true; }
            }
            if (repaint) Invalidate();

            if (_finished && !_hover && now >= _closeAt) BeginClose();   // fare ustundeyken (ayrintilar acik olsa da) kapanmaz
        }

        private async Task RunAsync()
        {
            _engine = new CleanEngine(_s);
            _targets = _engine.BuildTargets();

            SetState(ToastPhase.Scanning, _s.Preview ? "Önizleme için taranıyor…" : "Taranıyor…", "Geçici dosyalar aranıyor", "", "", -1);
            await Task.Run(() => _engine.Scan(_targets, () => false));

            if (_engine.TotalFiles == 0 && _engine.TotalBytes == 0)
            {
                SetState(ToastPhase.AlreadyClean, "Her şey temiz", "Silinecek geçici dosya bulunamadı", "", "", 1);
                _engine.WriteLog(_targets);
                Finish();
                return;
            }

            if (_s.Preview)
            {
                SetState(ToastPhase.Preview, "Önizleme hazır", Num(_engine.TotalFiles) + " dosya silinebilir · hiçbir şey silinmedi",
                    Util.HumanSize(_engine.TotalBytes), "bulundu", 1);
                _engine.WriteLog(_targets);
                Finish();
                return;
            }

            // gercek temizlik
            int total = _engine.TotalFiles;
            SetState(ToastPhase.Cleaning, "Temizleniyor…", "0 / " + Num(total) + " dosya", "0 B", "boşaldı", 0);
            var progress = new Progress<int>(v =>
            {
                if (_st.Phase != ToastPhase.Cleaning) return;
                _st.Progress = total > 0 ? Math.Min(1.0, (double)v / total) : 1;
                _st.Detail = Num(Math.Min(v, total)) + " / " + Num(total) + " dosya";
                _st.BigValue = Util.HumanSize(_engine.BytesFreed);
                Invalidate();
            });
            await Task.Run(() => _engine.Clean(progress, () => false));
            _engine.WriteLog(_targets);

            var parts = new List<string>();
            if (_engine.FilesDeleted > 0) parts.Add(Num(_engine.FilesDeleted) + " dosya");
            if (_engine.FoldersDeleted > 0) parts.Add(Num(_engine.FoldersDeleted) + " klasör");
            string detail = parts.Count > 0 ? string.Join(" · ", parts) + " silindi" : "Dosyalar kullanımda, silinemedi";
            SetState(ToastPhase.Done, "Temizlik tamamlandı", detail, Util.HumanSize(_engine.BytesFreed), "boşaldı", 1);

            var notes = new List<string>();
            if (_engine.ScheduledForReboot > 0) notes.Add(Num(_engine.ScheduledForReboot) + " dosya yeniden başlatınca silinecek");
            if (_engine.Errors > 0) notes.Add(Num(_engine.Errors) + " öğe silinemedi");
            _st.Footnote = string.Join(" · ", notes);
            Finish();
        }

        private void SetState(ToastPhase phase, string headline, string detail, string big, string bigCaption, double progress)
        {
            _st.Phase = phase;
            _st.Headline = headline;
            _st.Detail = detail;
            _st.BigValue = big;
            _st.BigCaption = bigCaption;
            _st.Progress = progress;
            Invalidate();
        }

        private void Finish()
        {
            _finished = true;
            _st.Rows.Clear();
            foreach (var src in _engine.Sources)
            {
                long v = _s.Preview ? src.Found : src.Freed;
                _st.Rows.Add(new ToastRow
                {
                    Label = src.Label,
                    Value = src.Skip != null ? "atlandı" : Util.HumanSize(v),
                    Note = src.Skip != null ? Cap(src.Skip) : "",
                    Dim = src.Skip != null || v == 0
                });
            }
            double sec = _s.AutoCloseSeconds <= 0 ? 1.5 : _s.AutoCloseSeconds;
            _closeAt = DateTime.Now.AddSeconds(sec);
            Invalidate();
        }

        private void BeginClose()
        {
            if (_closing) return;
            _closing = true;
            _closeStart = DateTime.Now;
            if (!_anim.Enabled) _anim.Start();
        }

        private static string Num(int n) { return n.ToString("N0", Tr); }
        private static string Cap(string t) { return t.Length == 0 ? t : char.ToUpper(t[0], Tr) + t.Substring(1); }

        public int ExitCode
        {
            get
            {
                if (_engine == null) return 0;
                if (_s.Preview) return _engine.TotalBytes > 0 || _engine.TotalFiles > 0 ? 0 : 2;   // sessiz moddaki onizleme ile ayni
                if (_engine.Errors > 0) return 1;
                if (_engine.FilesDeleted == 0 && _engine.FoldersDeleted == 0 && _engine.ScheduledForReboot == 0 && _engine.DeliveryFreed == 0) return 2;
                return 0;
            }
        }
    }

    // ---------------------- Giris ----------------------
    internal static class Program
    {
        private static int _exitCode = 0;

        [STAThread]
        private static int Main(string[] args)
        {
            var s = Settings.Load(args);

            // yonetici degilsek kendimizi standart UAC onayiyla yeniden baslat ve onun sonucunu don
            if (!s.Elevated && !CleanEngine.IsAdmin())
            {
                int code;
                if (TryRunElevated(args, out code)) return code;
                // UAC reddedildi: sinirli modda (sadece kullanicinin kendi klasorleri) devam
            }

            // tek kopya
            bool createdNew;
            using (var mutex = new Mutex(true, "SPCleanerPlus_SingleInstance_v2", out createdNew))
            {
                if (!createdNew) return -1;

                if (s.Silent)
                    return RunSilent(s);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new CleanForm(s);
                form.FormClosed += (a, b) => { _exitCode = form.ExitCode; };
                Application.Run(form);
                return _exitCode;
            }
        }

        private static bool TryRunElevated(string[] args, out int exitCode)
        {
            exitCode = 0;
            try
            {
                string a = string.Join(" ", args.Select(QuoteArg)) + " /ELEVATED";
                var psi = new ProcessStartInfo(Application.ExecutablePath, a.Trim()) { UseShellExecute = true, Verb = "runas" };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit();
                    exitCode = p.ExitCode;
                    return true;
                }
            }
            catch (Win32Exception) { return false; }   // 1223 = kullanici UAC'de "Hayir" dedi
            catch { return false; }
        }

        private static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        private static int RunSilent(Settings s)
        {
            try
            {
                var engine = new CleanEngine(s);
                var targets = engine.BuildTargets();
                engine.Scan(targets, () => false);
                if (s.Preview)
                {
                    engine.WriteLog(targets);
                    return engine.TotalBytes > 0 || engine.TotalFiles > 0 ? 0 : 2;
                }
                engine.Clean(null, () => false);
                engine.WriteLog(targets);
                if (engine.Errors > 0) return 1;
                if (engine.FilesDeleted == 0 && engine.FoldersDeleted == 0 && engine.ScheduledForReboot == 0 && engine.DeliveryFreed == 0) return 2;
                return 0;
            }
            catch { return 1; }
        }
    }
}
