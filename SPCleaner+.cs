// SPCleaner+ - Gecici (Temp) klasorlerini, eski hata raporlarini ve cokme dokumlerini temizleyen tek-exe arac.
// Derleme: build.bat (bkz. ayni klasor)
// Kullanim (ayar dosyasi yok, ayarlar exe icinde sabit):
//   Cift tik      : Pencereli, otomatik temizler, bitince kapanir (UAC onayi ister)
//   /S            : Sessiz (penceresiz)            /PREVIEW     : Silmez, sadece hesaplar
//   /LOG          : Exe yanina log yazar           /AGE:24      : 24 saatten eski dosyalar
//   /CURRENTUSER  : Sadece su anki kullanici       /NOREBOOT    : Kilitli dosyalari yeniden baslatmaya birakma
//   /EXCLUDE:a;b  : Haric tutulacak desenler (* ve ? kullanilabilir)
//   Yonetici izni : Acilista UAC onayi istenir (kendini "runas" ile yeniden baslatir). Reddedilirse
//                   sadece kullanicinin kendi Temp/hata raporu klasorleri temizlenir ("sinirli mod").
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
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]

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

    // ---------------------- Hedef ----------------------
    internal class Target
    {
        public string Path;
        public bool IsFile;        // klasor yerine tek dosya (or. MEMORY.DMP)
        public int MinAgeHours;    // bundan yeni dosyalara dokunma (0 = hepsi)

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

        // bulunan ogeler
        private readonly List<string> _files = new List<string>();
        private readonly List<long> _fileSizes = new List<long>();
        private readonly List<string> _dirs = new List<string>();         // normal alt klasorler (en derin once silinir)
        private readonly List<string> _reparse = new List<string>();      // junction/symlink -> sadece baglanti silinir, icine girilmez

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

            Action<string, int> addDir = (p, age) =>
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try
                {
                    string full = Path.GetFullPath(p.Trim());
                    if (!Directory.Exists(full)) return;
                    try { full = new DirectoryInfo(full).FullName; } catch { }
                    string norm = Norm(full);
                    if (IsProtected(full)) return;      // guvenlik: kritik klasor hedef olamaz
                    if (!set.ContainsKey(norm)) set[norm] = new Target { Path = full, MinAgeHours = age };
                }
                catch { }
            };
            Action<string> add = p => addDir(p, 0);

            Action<string, int> addFile = (p, age) =>
            {
                try
                {
                    string full = Path.GetFullPath(p);
                    if (!File.Exists(full)) return;
                    string norm = Norm(full);
                    if (!set.ContainsKey(norm)) set[norm] = new Target { Path = full, IsFile = true, MinAgeHours = age };
                }
                catch { }
            };

            // kullanici profiline bagli hedefler: hata raporlari + uygulama cokme dokumleri
            Action<string> addUserCrashData = localAppData =>
            {
                if (string.IsNullOrEmpty(localAppData)) return;
                addDir(Path.Combine(localAppData, @"Microsoft\Windows\WER\ReportQueue"), crashAge);
                addDir(Path.Combine(localAppData, @"Microsoft\Windows\WER\ReportArchive"), crashAge);
                addDir(Path.Combine(localAppData, "CrashDumps"), crashAge);
            };

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (_admin) add(Path.Combine(winDir, "Temp"));   // sinirli modda zaten silinemez
            add(Path.GetTempPath());
            add(Environment.GetEnvironmentVariable("TEMP"));
            add(Environment.GetEnvironmentVariable("TMP"));

            // sistem geneli hata raporlari (WER) ve cokme dokumleri (yonetici gerekir)
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (_admin)
            {
                if (!string.IsNullOrEmpty(programData))
                {
                    addDir(Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"), crashAge);
                    addDir(Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"), crashAge);
                    addDir(Path.Combine(programData, @"Microsoft\Windows\WER\Temp"), crashAge);
                }
                addDir(Path.Combine(winDir, "Minidump"), crashAge);
                addFile(Path.Combine(winDir, "MEMORY.DMP"), crashAge);
            }
            addUserCrashData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            // kullanicinin kayit defterindeki TEMP/TMP
            try
            {
                using (var rk = Registry.CurrentUser.OpenSubKey("Environment"))
                {
                    if (rk != null)
                    {
                        add(ExpandEnv(rk.GetValue("TEMP") as string));
                        add(ExpandEnv(rk.GetValue("TMP") as string));
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
                            add(Path.Combine(dir, @"AppData\Local\Temp"));
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
                if (t.IsFile) AddFile(t.Path, age);
                else Walk(t.Path, age, cancel);
            }
            // en derin klasorler once silinsin
            _dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        private void AddFile(string f, int ageHours)
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
            TotalBytes += len;
        }

        private void Walk(string dir, int ageHours, Func<bool> cancel)
        {
            if (cancel != null && cancel()) return;

            foreach (var f in SafeGetFiles(dir)) AddFile(f, ageHours);

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
                    _reparse.Add(d);
                    continue;
                }
                _dirs.Add(d);
                Walk(d, ageHours, cancel);
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
                try
                {
                    ClearAttributes(f);
                    File.Delete(f);
                    FilesDeleted++;
                    BytesFreed += size;
                }
                catch (Exception ex)
                {
                    if (_s.ScheduleReboot && _admin && TryScheduleDelete(f)) ScheduledForReboot++;
                    else { Errors++; LogErr(f, ex); }
                }
                done++;
                if (progress != null && (done % 16 == 0 || done == _files.Count)) progress.Report(done);
            }
            if (progress != null) progress.Report(_files.Count);

            // junction/symlink baglantilarini kaldir (hedef veriye dokunmaz); once bunlar ki ust klasorleri bosalsin
            foreach (var link in _reparse)
            {
                if (cancel != null && cancel()) break;
                if (TryDeleteLink(link)) FoldersDeleted++;
            }
            // bos kalan klasorleri sil (en derin once)
            foreach (var d in _dirs)
            {
                if (cancel != null && cancel()) break;
                if (TryDeleteDir(d)) FoldersDeleted++;
            }
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
            if (st.Footnote.Length > 0)
                Text(g, st.Footnote, UiFont("Segoe UI", S(11.5f)), th.SubText, new RectangleF(S(60), S(81), S(W - 76), S(18)), StringAlignment.Near);

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
        private int _homeTop;
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
            _homeTop = wa.Bottom - Height - m;
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
            if (h != _st.CloseHover) { _st.CloseHover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            if (_st.CloseHover) { _st.CloseHover = false; Cursor = Cursors.Default; Invalidate(); }
            // fare ustundeyken kapanmaz; ayrilinca biraz daha bekler
            if (_finished && _closeAt < DateTime.Now.AddSeconds(1.5)) _closeAt = DateTime.Now.AddSeconds(1.5);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_finished && e.Button == MouseButtons.Left && ToastPainter.CloseRect(_sc).Contains(e.Location)) BeginClose();
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

            if (_finished && !_hover && now >= _closeAt) BeginClose();
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

        public int ExitCode
        {
            get
            {
                if (_engine == null) return 0;
                if (_s.Preview) return _engine.TotalFiles > 0 ? 0 : 2;   // sessiz moddaki onizleme ile ayni
                if (_engine.Errors > 0) return 1;
                if (_engine.FilesDeleted == 0 && _engine.FoldersDeleted == 0 && _engine.ScheduledForReboot == 0) return 2;
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
                    return engine.TotalFiles > 0 ? 0 : 2;
                }
                engine.Clean(null, () => false);
                engine.WriteLog(targets);
                if (engine.Errors > 0) return 1;
                if (engine.FilesDeleted == 0 && engine.FoldersDeleted == 0 && engine.ScheduledForReboot == 0) return 2;
                return 0;
            }
            catch { return 1; }
        }
    }
}
