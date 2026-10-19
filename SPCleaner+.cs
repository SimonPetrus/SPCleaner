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
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

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
            return b.ToString(fmt, CultureInfo.InvariantCulture) + " " + u[i];
        }
    }

    // ---------------------- Pencere ----------------------
    internal class CleanForm : Form
    {
        private readonly Settings _s;
        private readonly Label _title = new Label();
        private readonly Label _status = new Label();
        private readonly Label _sizeLbl = new Label();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly System.Windows.Forms.Timer _closeTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _fadeTimer = new System.Windows.Forms.Timer();

        private CleanEngine _engine;
        private List<Target> _targets;

        public CleanForm(Settings s)
        {
            _s = s;
            InitUi();
        }

        private void InitUi()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Width = 380; Height = 150;
            BackColor = Color.FromArgb(28, 30, 36);
            Font = new Font("Segoe UI", 9f);
            Opacity = 0.97;

            var screen = Screen.PrimaryScreen.WorkingArea;
            Left = screen.Right - Width - 16;
            Top = screen.Bottom - Height - 16;

            _title.Text = "SPCleaner+";
            Text = "SPCleaner+";
            _title.ForeColor = Color.White;
            _title.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
            _title.AutoSize = false;
            _title.SetBounds(18, 14, Width - 36, 28);

            _status.Text = "Hazirlaniyor...";
            _status.ForeColor = Color.FromArgb(200, 205, 215);
            _status.AutoSize = false;
            _status.SetBounds(18, 48, Width - 36, 22);

            _bar.SetBounds(18, 78, Width - 36, 16);
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;

            _sizeLbl.Text = "";
            _sizeLbl.ForeColor = Color.FromArgb(120, 200, 140);
            _sizeLbl.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            _sizeLbl.AutoSize = false;
            _sizeLbl.TextAlign = ContentAlignment.MiddleRight;
            _sizeLbl.SetBounds(18, 104, Width - 36, 26);

            Controls.Add(_title);
            Controls.Add(_status);
            Controls.Add(_bar);
            Controls.Add(_sizeLbl);

            Load += async (a, b) => await RunAsync();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyRoundRegion();
        }

        private void ApplyRoundRegion()
        {
            try
            {
                int r = 16;
                var gp = new GraphicsPath();
                gp.AddArc(0, 0, r, r, 180, 90);
                gp.AddArc(Width - r, 0, r, r, 270, 90);
                gp.AddArc(Width - r, Height - r, r, r, 0, 90);
                gp.AddArc(0, Height - r, r, r, 90, 90);
                gp.CloseAllFigures();
                Region = new Region(gp);
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(70, 75, 90)))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private async Task RunAsync()
        {
            _engine = new CleanEngine(_s);
            _targets = _engine.BuildTargets();
            if (!CleanEngine.IsAdmin()) _title.Text = "SPCleaner+ (sinirli mod)";

            SetStatus(_s.Preview ? "Taraniyor (onizleme)..." : "Taraniyor...");
            await Task.Run(() => _engine.Scan(_targets, () => false));

            _sizeLbl.Text = "Bulunan: " + Util.HumanSize(_engine.TotalBytes);

            if (_engine.TotalFiles == 0 && _engine.TotalBytes == 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = 1; _bar.Value = 1;
                SetStatus("Zaten temiz, silinecek bir sey yok.");
                _sizeLbl.ForeColor = Color.FromArgb(150, 160, 175);
                _sizeLbl.Text = "0 B";
                Finish();
                return;
            }

            if (_s.Preview)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = 1; _bar.Value = 1;
                SetStatus(_engine.TotalFiles + " dosya silinecek (onizleme)");
                _engine.WriteLog(_targets);
                Finish();
                return;
            }

            // gercek temizlik
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Minimum = 0;
            _bar.Maximum = Math.Max(1, _engine.TotalFiles);
            _bar.Value = 0;
            SetStatus("Temizleniyor...");

            var progress = new Progress<int>(v =>
            {
                int val = Math.Min(_bar.Maximum, v);
                _bar.Value = val;
                _sizeLbl.Text = Util.HumanSize(_engine.BytesFreed) + " bosaldi";
            });

            await Task.Run(() => _engine.Clean(progress, () => false));

            _bar.Value = _bar.Maximum;
            _engine.WriteLog(_targets);

            // sonuc metni
            var parts = new List<string>();
            if (_engine.FilesDeleted > 0) parts.Add(_engine.FilesDeleted + (_engine.FilesDeleted == 1 ? " dosya" : " dosya"));
            if (_engine.FoldersDeleted > 0) parts.Add(_engine.FoldersDeleted + " klasor");
            string what = parts.Count > 0 ? string.Join(", ", parts) + " silindi" : "Silinecek bir sey yoktu";
            if (_engine.ScheduledForReboot > 0) what += "  (+" + _engine.ScheduledForReboot + " yeniden baslatmada)";
            SetStatus(what);
            _sizeLbl.Text = Util.HumanSize(_engine.BytesFreed) + " bosaldi";

            Finish();
        }

        private void Finish()
        {
            if (_s.AutoCloseSeconds <= 0) { _closeTimer.Interval = 1500; }
            else { _closeTimer.Interval = _s.AutoCloseSeconds * 1000; }
            _closeTimer.Tick += (a, b) => { _closeTimer.Stop(); StartFade(); };
            _closeTimer.Start();
        }

        private void StartFade()
        {
            _fadeTimer.Interval = 30;
            _fadeTimer.Tick += (a, b) =>
            {
                if (Opacity <= 0.08) { _fadeTimer.Stop(); Close(); }
                else Opacity -= 0.07;
            };
            _fadeTimer.Start();
        }

        private void SetStatus(string t) { _status.Text = t; }

        public int ExitCode
        {
            get
            {
                if (_engine == null) return 0;
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
