// =====================================================================
//  مهامي - ويدجت مهام TickTick ثابت على سطح المكتب (Windows, WPF, .NET Framework 4.8)
//  البيانات من TickTick Open API. Claude بيضيف ويعدّل المهام عن طريق TickTick نفسه.
//  البناء: build.cmd (بيستعمل csc الموجود بالويندوز، C# 5)
// =====================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AnasTick
{
    // -----------------------------------------------------------------
    //  أدوات عامة
    // -----------------------------------------------------------------
    static class Util
    {
        public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        public static readonly string[] Days = { "الأحد", "الإثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
        public static readonly string[] Months = { "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر" };

        public static string P(string name) { return Path.Combine(Dir, name); }

        public static void Log(string msg)
        {
            try { File.AppendAllText(P("error.log"), "[" + DateTime.Now.ToString("s", Inv) + "] " + msg + "\r\n", new UTF8Encoding(false)); }
            catch { }
        }

        // كتابة ذرّية: ملف مؤقت ثم استبدال
        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch { File.Delete(path); }
            }
            File.Move(tmp, path);
        }

        public static string S(IDictionary<string, object> d, string key)
        {
            object o;
            if (d != null && d.TryGetValue(key, out o) && o != null) return Convert.ToString(o, Inv);
            return null;
        }

        public static List<object> Arr(object o)
        {
            var list = new List<object>();
            var e = o as IEnumerable;
            if (e == null || o is string) return list;
            foreach (var x in e) list.Add(x);
            return list;
        }

        public static string Clock(DateTime t) { return t.ToString("h:mm", Inv) + (t.Hour < 12 ? " ص" : " م"); }

        public static string DayLabel(DateTime t)
        {
            int d = (t.Date - DateTime.Today).Days;
            if (d == 0) return "اليوم";
            if (d == -1) return "أمس";
            if (d == 1) return "غداً";
            return Days[(int)t.DayOfWeek] + " " + t.Day + " " + Months[t.Month - 1];
        }

        public static string When(DateTime t) { return DayLabel(t) + " الساعة " + Clock(t); }

        public static string DaysText(int n)
        {
            if (n == 1) return "يوم";
            if (n == 2) return "يومين";
            if (n <= 10) return n + " أيام";
            return n + " يوم";
        }

        public static string Stamp(DateTime t) { return t.ToString("yyyy-MM-ddTHH:mm", Inv); }

        public static DateTime? FromStamp(string s)
        {
            DateTime d;
            if (s != null && DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm", Inv, DateTimeStyles.None, out d)) return d;
            return null;
        }

        // صيغة TickTick: 2026-10-08T07:00:00+0000
        public static DateTime? ParseTT(string s, bool allDay)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var m = Regex.Match(s, @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.\d+)?(Z|[+-]\d{2}:?\d{2})$");
            if (!m.Success) return null;
            string tz = m.Groups[2].Value;
            if (tz == "Z") tz = "+00:00";
            else if (tz.Length == 5) tz = tz.Substring(0, 3) + ":" + tz.Substring(3);
            DateTimeOffset dto;
            if (!DateTimeOffset.TryParseExact(m.Groups[1].Value + tz, "yyyy-MM-dd'T'HH:mm:sszzz", Inv, DateTimeStyles.None, out dto)) return null;
            DateTime local = dto.LocalDateTime;
            // مهمة طول اليوم: منتصف الليل بمنطقة المهمة، فبنقرّب لأقرب منتصف ليل محلي
            if (allDay) return local.AddHours(12).Date;
            return local;
        }

        public static string ToTT(DateTime t)
        {
            TimeSpan off = TimeZoneInfo.Local.GetUtcOffset(t);
            string sign = off < TimeSpan.Zero ? "-" : "+";
            off = off.Duration();
            return t.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + sign + off.Hours.ToString("00") + off.Minutes.ToString("00");
        }

        static readonly SolidColorBrush Fallback = Brushes.White;
        static readonly Dictionary<string, SolidColorBrush> brushCache = new Dictionary<string, SolidColorBrush>();
        public static SolidColorBrush B(string hex)
        {
            SolidColorBrush b;
            if (brushCache.TryGetValue(hex, out b)) return b;
            try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); }
            catch { b = Fallback; }
            brushCache[hex] = b;
            return b;
        }

        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log("open: " + ex.Message); }
        }
    }

    // -----------------------------------------------------------------
    //  الإعدادات (settings.json اختياري)
    // -----------------------------------------------------------------
    class Settings
    {
        public string TimeZone = "Asia/Gaza";
        public double Width = 360;
        public double Opacity = 0.5;
        public string Style = "mica";     // mica (خفيف، مثل بطاقة الطقس) | glass (زجاج مموّه)
        public int RefreshMinutes = 3;
        public double MaxListHeight = 470;
        public bool SoundOn = true;
        public string Tone = "bell";      // bell | digital | soft
        public double Volume = 0.8;       // 0..1
        public int RingSeconds = 90;      // 0 = لحد ما تتصرف

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                string f = Util.P("settings.json");
                if (!File.Exists(f)) return s;
                var d = Util.Json.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) return s;
                string v;
                if ((v = Util.S(d, "timeZone")) != null) s.TimeZone = v;
                double x; int n;
                if ((v = Util.S(d, "width")) != null && double.TryParse(v, NumberStyles.Float, Util.Inv, out x)) s.Width = Math.Max(260, Math.Min(600, x));
                if ((v = Util.S(d, "opacity")) != null && double.TryParse(v, NumberStyles.Float, Util.Inv, out x)) s.Opacity = Math.Max(0, Math.Min(1, x));
                if ((v = Util.S(d, "refreshMinutes")) != null && int.TryParse(v, out n)) s.RefreshMinutes = Math.Max(1, n);
                if ((v = Util.S(d, "maxListHeight")) != null && double.TryParse(v, NumberStyles.Float, Util.Inv, out x)) s.MaxListHeight = Math.Max(150, x);
                if ((v = Util.S(d, "style")) != null && (v == "mica" || v == "glass")) s.Style = v;
                if ((v = Util.S(d, "sound")) != null) s.SoundOn = v == "True" || v == "true";
                if ((v = Util.S(d, "tone")) != null && (v == "bell" || v == "digital" || v == "soft")) s.Tone = v;
                if ((v = Util.S(d, "volume")) != null && double.TryParse(v, NumberStyles.Float, Util.Inv, out x)) s.Volume = Math.Max(0, Math.Min(1, x));
                if ((v = Util.S(d, "ringSeconds")) != null && int.TryParse(v, out n)) s.RingSeconds = Math.Max(0, n);
            }
            catch (Exception ex) { Util.Log("settings: " + ex.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                var d = new Dictionary<string, object>
                {
                    { "timeZone", TimeZone }, { "width", Width }, { "opacity", Opacity }, { "style", Style }, { "refreshMinutes", RefreshMinutes },
                    { "maxListHeight", MaxListHeight }, { "sound", SoundOn }, { "tone", Tone }, { "volume", Volume }, { "ringSeconds", RingSeconds }
                };
                Util.WriteAtomic(Util.P("settings.json"), Util.Json.Serialize(d));
            }
            catch (Exception ex) { Util.Log("settings save: " + ex.Message); }
        }
    }

    // -----------------------------------------------------------------
    //  حالة الويدجت (state.json)
    // -----------------------------------------------------------------
    public class PendingAlert
    {
        public string TaskId { get; set; }
        public string Title { get; set; }
        public string At { get; set; }
        public bool Missed { get; set; }
    }

    public class SnoozeEntry
    {
        public string TaskId { get; set; }
        public string Title { get; set; }
        public string At { get; set; }
    }

    public class WidgetState
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public bool Collapsed { get; set; }
        public bool UserExited { get; set; }
        public bool Initialized { get; set; }
        public string DoneDay { get; set; }
        public int DoneCount { get; set; }
        public List<string> Alerted { get; set; }
        public List<PendingAlert> Pending { get; set; }
        public List<SnoozeEntry> Snoozes { get; set; }

        public static WidgetState Load()
        {
            WidgetState s = null;
            try
            {
                string f = Util.P("state.json");
                if (File.Exists(f)) s = Util.Json.Deserialize<WidgetState>(File.ReadAllText(f, Encoding.UTF8));
            }
            catch (Exception ex) { Util.Log("state read: " + ex.Message); }
            if (s == null) s = new WidgetState();
            if (s.Alerted == null) s.Alerted = new List<string>();
            if (s.Pending == null) s.Pending = new List<PendingAlert>();
            if (s.Snoozes == null) s.Snoozes = new List<SnoozeEntry>();
            return s;
        }

        public void Save()
        {
            // التنبيهات الأقدم من 14 يوم ما إلها لزوم
            DateTime cut = DateTime.Now.AddDays(-14);
            Alerted.RemoveAll(k =>
            {
                int i = k.LastIndexOf('|');
                DateTime? t = i >= 0 ? Util.FromStamp(k.Substring(i + 1)) : null;
                return t == null || t.Value < cut;
            });
            try { Util.WriteAtomic(Util.P("state.json"), Util.Json.Serialize(this)); }
            catch (Exception ex) { Util.Log("state save: " + ex.Message); }
        }
    }

    // -----------------------------------------------------------------
    //  المهمة
    // -----------------------------------------------------------------
    class TodoTask
    {
        public string Id, ProjectId, Title, Content, Note, Link, Image, RepeatFlag;
        public int Priority;
        public bool AllDay;
        public DateTime? Due, Start;
        public List<string> Reminders = new List<string>();
        public Dictionary<string, object> Raw;
        public bool Temp;

        public static TodoTask From(Dictionary<string, object> d)
        {
            var t = new TodoTask();
            t.Raw = d;
            t.Id = Util.S(d, "id");
            t.ProjectId = Util.S(d, "projectId") ?? "";
            t.Title = Util.S(d, "title") ?? "";
            t.Content = Util.S(d, "content") ?? Util.S(d, "desc") ?? "";
            int p; int.TryParse(Util.S(d, "priority"), out p); t.Priority = p;
            t.AllDay = Util.S(d, "isAllDay") == "True";
            t.Due = Util.ParseTT(Util.S(d, "dueDate"), t.AllDay);
            t.Start = Util.ParseTT(Util.S(d, "startDate"), t.AllDay);
            t.RepeatFlag = Util.S(d, "repeatFlag");
            object r;
            if (d.TryGetValue("reminders", out r))
                foreach (var x in Util.Arr(r)) if (x != null) t.Reminders.Add(x.ToString());
            var m = Regex.Match(t.Content, @"https?://\S+");
            if (m.Success) t.Link = m.Value;
            var im = Regex.Match(t.Content, @"[A-Za-z]:\\[^\r\n]*?\.(?:png|jpe?g|webp|gif|bmp)", RegexOptions.IgnoreCase);
            if (im.Success) t.Image = im.Value;
            foreach (var line in t.Content.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length > 0 && !Regex.IsMatch(l, @"^https?://\S+$") && !l.StartsWith("الصورة:")) { t.Note = l; break; }
            }
            return t;
        }

        static readonly Regex TriggerRx = new Regex(@"^TRIGGER(?:;RELATED=(START|END))?:(-)?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");

        static int G(Match m, int i) { return m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0; }

        // أوقات التنبيه الفعلية حسب صيغة RFC 5545 يلي بيستعملها TickTick
        public IEnumerable<DateTime> Alarms()
        {
            foreach (var r in Reminders)
            {
                var m = TriggerRx.Match(r.Trim());
                if (!m.Success) continue;
                DateTime? b = m.Groups[1].Value == "END" ? (Due ?? Start) : (Start ?? Due);
                if (b == null) continue;
                var span = new TimeSpan(G(m, 3) * 7 + G(m, 4), G(m, 5), G(m, 6), G(m, 7));
                if (m.Groups[2].Success) span = span.Negate();
                yield return b.Value + span;
            }
        }
    }

    // -----------------------------------------------------------------
    //  TickTick Open API
    // -----------------------------------------------------------------
    class AuthException : Exception { }

    class Api
    {
        const string Base = "https://api.ticktick.com/open/v1";
        public const string Redirect = "http://localhost:8790/callback";
        public const string Scope = "tasks:read tasks:write";
        string token;

        public Api() { LoadToken(); }
        public bool HasToken { get { return !string.IsNullOrEmpty(token); } }

        void LoadToken()
        {
            try
            {
                string f = Util.P("token.dat");
                if (File.Exists(f))
                    token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { Util.Log("token read: " + ex.Message); }
        }

        void SaveToken(string t)
        {
            token = t;
            File.WriteAllBytes(Util.P("token.dat"), ProtectedData.Protect(Encoding.UTF8.GetBytes(t), null, DataProtectionScope.CurrentUser));
        }

        public void ClearToken()
        {
            token = null;
            try { File.Delete(Util.P("token.dat")); } catch { }
        }

        static async Task<string> Http(string method, string url, string auth, string contentType, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "AnasTickWidget/1.0";
            req.Accept = "application/json";
            if (auth != null) req.Headers[HttpRequestHeader.Authorization] = auth;
            Task<string> work = Run(req, contentType, body);
            if (await Task.WhenAny(work, Task.Delay(25000)) != work)
            {
                req.Abort();
                throw new TimeoutException("timeout " + url);
            }
            return await work;
        }

        static async Task<string> Run(HttpWebRequest req, string contentType, string body)
        {
            if (body != null)
            {
                req.ContentType = contentType;
                byte[] b = Encoding.UTF8.GetBytes(body);
                using (var s = await req.GetRequestStreamAsync()) await s.WriteAsync(b, 0, b.Length);
            }
            else if (req.Method == "POST") req.ContentLength = 0;

            WebException err;
            try
            {
                using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return await sr.ReadToEndAsync();
            }
            catch (WebException ex) { err = ex; }

            var r = err.Response as HttpWebResponse;
            if (r != null && r.StatusCode == HttpStatusCode.Unauthorized) throw new AuthException();
            string detail = "";
            if (r != null) { try { using (var sr = new StreamReader(r.GetResponseStream())) detail = sr.ReadToEnd(); } catch { } }
            throw new Exception(err.Message + " " + detail);
        }

        Task<string> Call(string method, string path, object body)
        {
            if (!HasToken) throw new AuthException();
            return Http(method, Base + path, "Bearer " + token, "application/json", body == null ? null : Util.Json.Serialize(body));
        }

        public async Task<List<TodoTask>> FetchAll()
        {
            var ids = new List<string> { "inbox" };
            foreach (var o in Util.Arr(Util.Json.DeserializeObject(await Call("GET", "/project", null))))
            {
                var d = o as Dictionary<string, object>;
                if (d == null || Util.S(d, "closed") == "True") continue;
                string id = Util.S(d, "id");
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }

            var jobs = ids.Select(id => Call("GET", "/project/" + Uri.EscapeDataString(id) + "/data", null)).ToList();
            var all = new List<TodoTask>();
            var seen = new HashSet<string>();
            int failed = 0;
            foreach (var job in jobs)
            {
                string txt;
                try { txt = await job; }
                catch (AuthException) { throw; }
                catch (Exception ex) { failed++; Util.Log("project data: " + ex.Message); continue; }

                var d = Util.Json.DeserializeObject(txt) as Dictionary<string, object>;
                object ts;
                if (d == null || !d.TryGetValue("tasks", out ts)) continue;
                foreach (var o in Util.Arr(ts))
                {
                    var td = o as Dictionary<string, object>;
                    if (td == null) continue;
                    string status = Util.S(td, "status");
                    if (status != null && status != "0") continue;
                    var t = TodoTask.From(td);
                    if (t.Id != null && seen.Add(t.Id)) all.Add(t);
                }
            }
            if (failed == jobs.Count) throw new Exception("all project requests failed");
            return all;
        }

        public Task Complete(TodoTask t)
        {
            return Call("POST", "/project/" + Uri.EscapeDataString(t.ProjectId) + "/task/" + Uri.EscapeDataString(t.Id) + "/complete", null);
        }

        public Task Create(Dictionary<string, object> task) { return Call("POST", "/task", task); }

        public Task Update(string id, Dictionary<string, object> task) { return Call("POST", "/task/" + Uri.EscapeDataString(id), task); }

        public async Task Exchange(string code, string clientId, string secret)
        {
            string form = "code=" + Uri.EscapeDataString(code)
                        + "&grant_type=authorization_code"
                        + "&scope=" + Uri.EscapeDataString(Scope)
                        + "&redirect_uri=" + Uri.EscapeDataString(Redirect);
            string basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + secret));
            string res = await Http("POST", "https://ticktick.com/oauth/token", basic, "application/x-www-form-urlencoded", form);
            var d = Util.Json.DeserializeObject(res) as Dictionary<string, object>;
            string at = Util.S(d, "access_token");
            if (string.IsNullOrEmpty(at)) throw new Exception("no access_token in response");
            SaveToken(at);
        }
    }

    // -----------------------------------------------------------------
    //  Win32
    // -----------------------------------------------------------------
    static class Native
    {
        public const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, GWL_STYLE = -16;
        public const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        public const int WM_WINDOWPOSCHANGING = 0x46;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);

        // النافذة يلي فيها SHELLDLL_DefView (أيقونات سطح المكتب): Progman أو WorkerW
        public static IntPtr DesktopHost()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return progman;
            IntPtr w = IntPtr.Zero;
            while ((w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
                if (FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return w;
            return progman;
        }
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong32(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr h, int i, int v);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr64(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int i, IntPtr v);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        // الذاكرة (RAM + ملف التبديل) المتبقية بالنظام، بالـ MB
        public static long FreeCommitMB()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref m) ? (long)(m.ullAvailPageFile / (1024 * 1024)) : long.MaxValue;
        }

        public static IntPtr GetLong(IntPtr h, int i) { return IntPtr.Size == 8 ? GetWindowLongPtr64(h, i) : new IntPtr(GetWindowLong32(h, i)); }
        public static void SetLong(IntPtr h, int i, IntPtr v) { if (IntPtr.Size == 8) SetWindowLongPtr64(h, i, v); else SetWindowLong32(h, i, v.ToInt32()); }

        // تقليل الذاكرة المحجوزة بعد كل تحديث
        public static void Trim()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }
    }

    // -----------------------------------------------------------------
    //  زجاج حقيقي: نسخة مموّهة من خلفية الشاشة بتنرسم خلف اللوح
    // -----------------------------------------------------------------
    static class Glass
    {
        static System.Windows.Media.Imaging.BitmapSource image;
        static double scaleX, scaleY, offX, offY, screenW, screenH;

        public static void Load()
        {
            image = null;
            Version++;
            try
            {
                string path = null, style = null, tile = null;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    if (k != null) { path = k.GetValue("WallPaper") as string; style = k.GetValue("WallpaperStyle") as string; tile = k.GetValue("TileWallpaper") as string; }
                }
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (!File.Exists(path)) return;

                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bi.DecodePixelWidth = 480;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();

                double iw = bi.PixelWidth, ih = bi.PixelHeight;
                var img = new System.Windows.Controls.Image { Source = bi, Width = iw, Height = ih, Stretch = Stretch.Fill, Effect = new BlurEffect { Radius = 9, KernelType = KernelType.Gaussian } };
                img.Measure(new Size(iw, ih));
                img.Arrange(new Rect(0, 0, iw, ih));
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)iw, (int)ih, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(img);
                rtb.Freeze();

                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile | System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                double nw = dec.Frames[0].PixelWidth, nh = dec.Frames[0].PixelHeight;
                double ratio = nw / iw;   // بكسل شاشة لكل بكسل من النسخة الصغيرة، لما الصورة بحجمها الأصلي

                var b = Forms.Screen.PrimaryScreen.Bounds;
                screenW = b.Width; screenH = b.Height;
                bool native = (tile == "1" || style == "0") && nw >= screenW && nh >= screenH;
                if (native)
                {
                    // Tile أو Center: الصورة بحجمها الأصلي
                    scaleX = scaleY = ratio;
                    if (tile == "1") { offX = offY = 0; }
                    else { offX = (screenW - nw) / 2; offY = (screenH - nh) / 2; }
                }
                else if (style == "2") { scaleX = screenW / iw; scaleY = screenH / ih; offX = offY = 0; }
                else
                {
                    // Fill (10/22) أو Fit (6)
                    double sc = style == "6" ? Math.Min(screenW / iw, screenH / ih) : Math.Max(screenW / iw, screenH / ih);
                    scaleX = scaleY = sc;
                    offX = (screenW - iw * sc) / 2;
                    offY = (screenH - ih * sc) / 2;
                }
                image = rtb;
            }
            catch (Exception ex) { Util.Log("glass: " + ex.Message); image = null; }
        }

        // r بإحداثيات الشاشة بالبكسل
        public static int Version;
        public static bool Loaded { get { return image != null; } }
        public static void Unload() { image = null; Version++; }

        public static ImageBrush Create()
        {
            if (image == null) return null;
            return new ImageBrush(image) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        }

        // r بإحداثيات الشاشة بالبكسل. false = برا الشاشة الرئيسية
        public static bool Place(ImageBrush br, Rect r)
        {
            if (image == null || r.Left < 0 || r.Top < 0 || r.Right > screenW || r.Bottom > screenH) return false;
            br.Viewbox = new Rect((r.X - offX) / scaleX, (r.Y - offY) / scaleY, r.Width / scaleX, r.Height / scaleY);
            return true;
        }
    }

    // -----------------------------------------------------------------
    //  المنبه: صوت رنين يتكرر + نافذة تنبيه فوق كل شي
    // -----------------------------------------------------------------
    static class AlarmSound
    {
        static System.Windows.Media.MediaPlayer mp;
        static bool playing;

        public static void Start(Settings cfg)
        {
            if (playing || !cfg.SoundOn) return;
            try
            {
                string f = Util.P(@"sounds\" + cfg.Tone + ".wav");
                if (!File.Exists(f)) f = Util.P(@"sounds\bell.wav");
                if (!File.Exists(f)) { System.Media.SystemSounds.Exclamation.Play(); return; }
                var player = new System.Windows.Media.MediaPlayer();
                player.Volume = cfg.Volume;
                player.MediaEnded += delegate { try { player.Position = TimeSpan.Zero; player.Play(); } catch { } };
                player.Open(new Uri(f));
                player.Play();
                mp = player;
                playing = true;
            }
            catch (Exception ex) { Util.Log("alarm sound: " + ex.Message); }
        }

        public static void Stop()
        {
            if (!playing) return;
            playing = false;
            try { mp.Stop(); mp.Close(); } catch { }
            mp = null;
        }
    }

    class AlarmWindow : Window
    {
        public Action<PendingAlert> OnDone;
        public Action<PendingAlert, DateTime> OnSnooze;
        public Action OnDismiss;

        static readonly FontFamily Mdl2 = new FontFamily("Segoe MDL2 Assets");
        PendingAlert current;
        TextBlock kind, title, when, more;

        public AlarmWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FlowDirection = FlowDirection.RightToLeft;
            FontFamily = new FontFamily("thmanyah sans, Segoe UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Title = "تنبيه";
            Opacity = 0;
            Build();
            SizeChanged += delegate { Place(); };
            Loaded += delegate
            {
                Place();
                var slide = new TranslateTransform(0, -28);
                ((FrameworkElement)Content).RenderTransform = slide;
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-28, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            };
        }

        void Place()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 4;
        }

        static TextBlock Txt(double size, string color, bool bold)
        {
            var t = new TextBlock { FontSize = size, Foreground = Util.B(color), TextWrapping = TextWrapping.Wrap };
            if (bold) t.FontWeight = FontWeights.Bold;
            return t;
        }

        static Border Btn(string text, string bg, string hover, string fg, bool bold, Action act)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
            var from = (Color)ColorConverter.ConvertFromString(bg);
            var to = (Color)ColorConverter.ConvertFromString(hover);
            var t = Txt(13, fg, bold);
            t.Text = text;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.TextWrapping = TextWrapping.NoWrap;
            var b = new Border { Background = brush, CornerRadius = new CornerRadius(13), Padding = new Thickness(6, 11, 6, 12), Cursor = Cursors.Hand, Child = t, Margin = new Thickness(3, 0, 3, 0) };
            b.MouseEnter += delegate { brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(100))); };
            b.MouseLeave += delegate { brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, TimeSpan.FromMilliseconds(160))); };
            b.MouseLeftButtonUp += delegate { act(); };
            return b;
        }

        void Build()
        {
            var root = new Border
            {
                Width = 470, Margin = new Thickness(18), Padding = new Thickness(20, 18, 20, 18),
                CornerRadius = new CornerRadius(24),
                Background = Util.B("#F2151720"), BorderBrush = Util.B("#38FFFFFF"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black }
            };
            var stack = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pulse = new ScaleTransform(1, 1);
            var icon = new Border
            {
                Width = 50, Height = 50, CornerRadius = new CornerRadius(25), VerticalAlignment = VerticalAlignment.Top,
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#FF9F0A"), (Color)ColorConverter.ConvertFromString("#FF453A"), 45),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pulse,
                Child = new TextBlock { Text = "\uE121", FontFamily = Mdl2, FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var beat = new DoubleAnimation(1, 1.12, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            pulse.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
            pulse.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
            head.Children.Add(icon);

            var texts = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            kind = Txt(12, "#FF9F0A", true);
            title = Txt(20, "#FFFFFF", true);
            title.Margin = new Thickness(0, 2, 0, 0);
            when = Txt(12.5, "#A6FFFFFF", false);
            when.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(kind);
            texts.Children.Add(title);
            texts.Children.Add(when);
            Grid.SetColumn(texts, 1);
            head.Children.Add(texts);

            var close = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, ToolTip = "إسكات" };
            close.Child = new TextBlock { Text = "\uE711", FontFamily = Mdl2, FontSize = 11, Foreground = Util.B("#B3FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            close.MouseEnter += delegate { close.Background = Util.B("#2EFFFFFF"); };
            close.MouseLeave += delegate { close.Background = Brushes.Transparent; };
            close.MouseLeftButtonUp += delegate { if (OnDismiss != null) OnDismiss(); };
            Grid.SetColumn(close, 2);
            head.Children.Add(close);
            stack.Children.Add(head);

            more = Txt(12, "#A6FFFFFF", false);
            more.Margin = new Thickness(64, 8, 0, 0);
            stack.Children.Add(more);

            var row = new Grid { Margin = new Thickness(-3, 16, -3, 0) };
            double[] widths = { 1.35, 1, 1, 1.15 };
            foreach (var w in widths) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            var b0 = Btn("خلصت", "#32D74B", "#5BE271", "#0B1A10", true, delegate { if (OnDone != null && current != null) OnDone(current); });
            var b1 = Btn("10 دقايق", "#2EFFFFFF", "#47FFFFFF", "#FFFFFF", false, delegate { if (OnSnooze != null && current != null) OnSnooze(current, DateTime.Now.AddMinutes(10)); });
            var b2 = Btn("ساعة", "#2EFFFFFF", "#47FFFFFF", "#FFFFFF", false, delegate { if (OnSnooze != null && current != null) OnSnooze(current, DateTime.Now.AddHours(1)); });
            var b3 = Btn("غداً 9 ص", "#2EFFFFFF", "#47FFFFFF", "#FFFFFF", false, delegate { if (OnSnooze != null && current != null) OnSnooze(current, DateTime.Today.AddDays(1).AddHours(9)); });
            Grid.SetColumn(b1, 1); Grid.SetColumn(b2, 2); Grid.SetColumn(b3, 3);
            row.Children.Add(b0); row.Children.Add(b1); row.Children.Add(b2); row.Children.Add(b3);
            stack.Children.Add(row);

            root.Child = stack;
            Content = root;
        }

        public void Set(PendingAlert p, int extra)
        {
            current = p;
            kind.Text = p.Missed ? "فاتك تذكير" : "تذكير";
            kind.Foreground = Util.B(p.Missed ? "#FF453A" : "#FF9F0A");
            title.Text = p.Title;
            when.Text = (p.Missed ? "كان " : "") + Util.When(Util.FromStamp(p.At) ?? DateTime.Now);
            more.Text = extra > 0 ? "+ " + extra + (extra == 1 ? " تذكير آخر" : " تذكيرات أخرى") : "";
            more.Visibility = extra > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void FadeOut()
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            a.Completed += delegate { Close(); };
            BeginAnimation(OpacityProperty, a);
        }
    }

    enum Lvl { Over = 0, Today = 1, Urgent = 2, Soon = 3, Ok = 4, None = 5 }

    class DueInfo { public string Text; public Lvl Level; }

    // -----------------------------------------------------------------
    //  النافذة
    // -----------------------------------------------------------------
    class Widget : Window
    {
        const string ResXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='5'/>
    <Setter Property='MinWidth' Value='5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border CornerRadius='2.5' Background='#38FFFFFF'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='IconBtn' TargetType='Button'>
    <Setter Property='Width' Value='28'/>
    <Setter Property='Height' Value='28'/>
    <Setter Property='Foreground' Value='#B8FFFFFF'/>
    <Setter Property='FontFamily' Value='Segoe MDL2 Assets'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='Transparent' CornerRadius='7'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#2EFFFFFF'/>
              <Setter Property='Foreground' Value='#FFFFFF'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Pill' TargetType='Button'>
    <Setter Property='Foreground' Value='#F2FFFFFF'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='#2EFFFFFF' CornerRadius='9' Padding='11,4,11,5'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#40FFFFFF'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='Input' TargetType='TextBox'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='CaretBrush' Value='White'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='bd' Background='Transparent' BorderBrush='Transparent' BorderThickness='0' CornerRadius='9' Padding='8,7,8,7'>
            <ScrollViewer x:Name='PART_ContentHost' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='#14FFFFFF'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='#1FFFFFFF'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        static readonly Dictionary<Lvl, string> LvlColor = new Dictionary<Lvl, string>
        {
            { Lvl.Over, "#FF453A" }, { Lvl.Today, "#FF9F0A" }, { Lvl.Urgent, "#FF453A" },
            { Lvl.Soon, "#FFD60A" }, { Lvl.Ok, "#32D74B" }, { Lvl.None, "#98989D" }
        };
        const string Green = "#32D74B";
        static readonly FontFamily Mdl2 = new FontFamily("Segoe MDL2 Assets");

        readonly WidgetState st;
        readonly Settings cfg;
        readonly Api api = new Api();
        List<TodoTask> tasks;                 // null = لسا ما انحمّلت
        DateTime? lastOk;
        string statusText;
        bool needAuth, refreshing, connecting, exiting;
        readonly Dictionary<string, DispatcherTimer> pendingDone = new Dictionary<string, DispatcherTimer>();
        DateTime renderDay, lastTick, lastRefreshTry = DateTime.MinValue;
        int tickCount;
        IntPtr hwnd;
        DispatcherTimer timer, attachRetry;
        Forms.NotifyIcon tray;
        Forms.ContextMenuStrip menu;

        Style iconStyle, pillStyle;
        TextBlock titleText, countText, summary, statusLine, newHint;
        Border rootBorder, tintLayer, sheenLayer;
        SolidColorBrush micaBrush;
        ImageBrush glassBrush;
        AlarmWindow alarmWin;
        DispatcherTimer alarmStop;
        readonly HashSet<string> silenced = new HashSet<string>(), rung = new HashSet<string>();
        int glassVer = -1;
        string lastSig;
        bool editing;
        readonly HashSet<string> expanded = new HashSet<string>();
        bool rendered;
        readonly HashSet<string> shown = new HashSet<string>();
        static readonly Brush FallbackGlass = Util.B("#E60C0E16");
        StackPanel alertsPanel, listPanel, body;
        TextBox newBox;
        Button btnQuick, btnSettings;
        enum Mode { List, Quick, Settings }
        Mode mode = Mode.List;
        StackPanel panelHost;
        ScrollViewer listScroll;
        Border hairline;
        Grid addGrid;
        SolidColorBrush tint;
        DispatcherTimer flashTimer, previewTimer;

        public Widget(WidgetState state, Settings settings)
        {
            st = state;
            cfg = settings;
            foreach (var old in st.Pending) { silenced.Add(old.TaskId + "|" + old.At); rung.Add(old.TaskId + "|" + old.At); }  // اللي كان معروض قبل إعادة التشغيل ما بيرن كمان مرة

            Title = "مهامي";
            Width = cfg.Width;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            FlowDirection = FlowDirection.RightToLeft;
            FontFamily = new FontFamily("thmanyah sans, Segoe UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Opacity = 0;

            Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(ResXaml));
            iconStyle = (Style)Resources["IconBtn"];
            pillStyle = (Style)Resources["Pill"];

            if (cfg.Style == "glass") Glass.Load();
            BuildUi();
            BuildTray();

            SourceInitialized += OnSourceInitialized;
            LocationChanged += delegate { ApplyGlass(); };
            SizeChanged += delegate { ApplyGlass(); };
            Loaded += delegate { BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))); };
            Closed += OnClosed;
            MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.OriginalSource is TextBox || newBox.IsKeyboardFocusWithin) return;
                menu.Show(Forms.Cursor.Position);
                e.Handled = true;
            };

            tasks = LoadCache();
            if (!api.HasToken) needAuth = true;
            ApplyCollapse();
            Render();

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            Show();
            Dispatcher.BeginInvoke(new Action(Native.Trim), DispatcherPriority.ApplicationIdle);

            if (!api.HasToken && tasks == null) Dispatcher.BeginInvoke(new Action(Connect), DispatcherPriority.Background);
            else Dispatcher.BeginInvoke(new Action(() => { var _ = Refresh(); }), DispatcherPriority.Background);
        }

        // ----------------------------------------------------------- UI
        static TextBlock T(string text, double size, string color, bool bold = false)
        {
            var tb = new TextBlock { Text = text, FontSize = size, Foreground = Util.B(color), TextWrapping = TextWrapping.Wrap };
            if (bold) tb.FontWeight = FontWeights.Medium;
            return tb;
        }

        Button IconButton(string glyph, string tip, Action onClick)
        {
            var b = new Button { Style = iconStyle, Content = glyph, ToolTip = tip };
            b.Click += delegate { onClick(); };
            return b;
        }

        Button Pill(string text, Action onClick)
        {
            var b = new Button { Style = pillStyle, Content = text, Margin = new Thickness(6, 6, 0, 0) };
            b.Click += delegate { onClick(); };
            return b;
        }

        void BuildUi()
        {
            byte alpha = (byte)Math.Round(cfg.Opacity * 255);
            var root = new Border
            {
                CornerRadius = new CornerRadius(22),
                Background = FallbackGlass,
                BorderBrush = Util.B("#33FFFFFF"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Opacity = 0.3, Color = Colors.Black }
            };
            rootBorder = root;
            var dock = new DockPanel();
            var layers = new Grid();
            var tintMargin = new Thickness(-16, -14, -16, -14);
            tint = new SolidColorBrush(Color.FromArgb(alpha, 0x0A, 0x0C, 0x14));
            tintLayer = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(21), Margin = tintMargin, Background = tint };
            layers.Children.Add(tintLayer);
            sheenLayer = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(21), Margin = tintMargin, Background = new LinearGradientBrush(Color.FromArgb(0x2E, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90) };
            layers.Children.Add(sheenLayer);
            layers.Children.Add(dock);
            root.Child = layers;

            // الرأس
            var header = new Grid { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleText = T("", 14, "#FFFFFF", true);
            titleText.FontWeight = FontWeights.Bold;
            titleText.TextWrapping = TextWrapping.NoWrap;
            titleStack.Children.Add(titleText);
            countText = T("", 11, "#99FFFFFF");
            countText.TextWrapping = TextWrapping.NoWrap;
            countText.Margin = new Thickness(0, 1, 0, 0);
            titleStack.Children.Add(countText);
            header.Children.Add(titleStack);

            btnQuick = IconButton("\uE121", "تنبيه سريع", () => SetMode(Mode.Quick));
            btnSettings = IconButton("\uE713", "الإعدادات", () => SetMode(Mode.Settings));
            btnQuick.Opacity = btnSettings.Opacity = 0.85;
            var headBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headBtns.Children.Add(btnQuick);
            headBtns.Children.Add(btnSettings);
            Grid.SetColumn(headBtns, 1);
            header.Children.Add(headBtns);
            header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2) { ToggleCollapse(); return; }
                try { DragMove(); } catch { }
                st.Left = Left; st.Top = Top; st.Save();
            };
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            // ملخص وضع التصغير
            summary = T("", 12, "#E6FFFFFF");
            summary.TextWrapping = TextWrapping.NoWrap;
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            summary.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(summary, Dock.Top);
            dock.Children.Add(summary);

            // التنبيهات
            alertsPanel = new StackPanel();
            DockPanel.SetDock(alertsPanel, Dock.Top);
            dock.Children.Add(alertsPanel);

            // سطر الحالة (غير متصل، الربط...)
            statusLine = T("", 11, "#FFD60A");
            statusLine.Margin = new Thickness(2, 8, 2, 0);
            statusLine.MouseLeftButtonUp += delegate { if (needAuth) Connect(); else { var _ = Refresh(); } };
            statusLine.Cursor = Cursors.Hand;
            DockPanel.SetDock(statusLine, Dock.Top);
            dock.Children.Add(statusLine);

            // القائمة + الإضافة
            body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            listPanel = new StackPanel();
            listScroll = new ScrollViewer
            {
                MaxHeight = cfg.MaxListHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listPanel
            };
            body.Children.Add(listScroll);
            addGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            newBox = new TextBox { Style = (Style)Resources["Input"], ToolTip = "للموعد: @بكرا   @+3   @2026-10-08\nللتنبيه: @17:30   @10د   @ساعة" };
            newHint = T("＋  إضافة مهمة", 12, "#99FFFFFF");
            newHint.IsHitTestVisible = false;
            newHint.VerticalAlignment = VerticalAlignment.Center;
            newHint.Margin = new Thickness(9, 0, 9, 0);
            newHint.TextWrapping = TextWrapping.NoWrap;
            newBox.TextChanged += delegate { newHint.Visibility = newBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            newBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { AddFromBox(); e.Handled = true; }
                else if (e.Key == Key.Escape) { newBox.Text = ""; Keyboard.ClearFocus(); }
            };
            addGrid.Children.Add(newBox);
            addGrid.Children.Add(newHint);
            hairline = new Border { Height = 1, Background = Util.B("#1FFFFFFF"), Margin = new Thickness(2, 8, 2, 0) };
            body.Children.Add(hairline);
            body.Children.Add(addGrid);
            panelHost = new StackPanel { Visibility = Visibility.Collapsed };
            body.Children.Add(panelHost);
            dock.Children.Add(body);

            Content = root;
            ApplyStyle();
        }

        Forms.ToolStripMenuItem MenuItem(string text, Action onClick)
        {
            var mi = new Forms.ToolStripMenuItem(text);
            mi.Click += delegate { onClick(); };
            return mi;
        }

        void BuildTray()
        {
            menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes, ShowImageMargin = false, Font = new Drawing.Font("thmanyah sans", 10f) };
            menu.Items.Add(MenuItem("خروج", ExitApp));

            tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "مهامي", Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += delegate(object s, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Peek(); };
            tray.BalloonTipClicked += delegate { Peek(); };
        }

        static Drawing.Icon MakeIcon()
        {
            using (var bmp = new Drawing.Bitmap(32, 32))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                using (var path = new Drawing.Drawing2D.GraphicsPath())
                using (var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 59, 130, 246)))
                using (var pen = new Drawing.Pen(Drawing.Color.White, 3.6f))
                {
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float r = 16, x = 1, y = 1, w = 30, h = 30;
                    path.AddArc(x, y, r, r, 180, 90);
                    path.AddArc(x + w - r, y, r, r, 270, 90);
                    path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
                    path.AddArc(x, y + h - r, r, r, 90, 90);
                    path.CloseFigure();
                    g.FillPath(fill, path);
                    pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(pen, new[] { new Drawing.PointF(9, 16.5f), new Drawing.PointF(14, 21.5f), new Drawing.PointF(23, 11) });
                }
                return Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ----------------------------------------------------------- العرض
        DueInfo GetDue(TodoTask t)
        {
            DateTime now = DateTime.Now;
            if (t.Due == null) return new DueInfo { Text = "", Level = t.Priority >= 5 ? Lvl.Urgent : Lvl.None };
            DateTime d = t.Due.Value;
            int days = (d.Date - now.Date).Days;
            string time = t.AllDay ? "" : " · " + Util.Clock(d);
            if (days < 0) return new DueInfo { Text = "متأخر " + Util.DaysText(-days), Level = Lvl.Over };
            if (days == 0)
            {
                if (!t.AllDay && d < now) return new DueInfo { Text = "فات الموعد" + time, Level = Lvl.Over };
                return new DueInfo { Text = "اليوم" + time, Level = Lvl.Today };
            }
            if (days == 1) return new DueInfo { Text = "غداً" + time, Level = Lvl.Soon };
            if (days == 2) return new DueInfo { Text = "بعد يومين · " + Util.Days[(int)d.DayOfWeek], Level = Lvl.Soon };
            Lvl lvl = t.Priority >= 5 ? Lvl.Urgent : Lvl.Ok;
            if (days > 30) return new DueInfo { Text = d.Day + " " + Util.Months[d.Month - 1], Level = lvl };
            return new DueInfo { Text = "باقي " + Util.DaysText(days) + " · " + Util.Days[(int)d.DayOfWeek], Level = lvl };
        }

        static string TasksText(int n)
        {
            if (n == 1) return "مهمة وحدة";
            if (n == 2) return "مهمتين";
            if (n <= 10) return n + " مهام";
            return n + " مهمة";
        }

        // دائرة صغيرة: فارغة = غير مكتملة، ✓ = مكتملة
        FrameworkElement CheckDot(bool on, string ringColor, double size)
        {
            double box = size + 10;
            var g = new Grid { Width = box, Height = box, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, ToolTip = on ? "تراجع" : "خلصت" };
            var ring = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(1.5),
                BorderBrush = Util.B(on ? Green : ringColor), Background = on ? Util.B(Green) : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            if (on)
                ring.Child = new TextBlock { Text = "\uE73E", FontFamily = Mdl2, FontSize = size * 0.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(ring);
            if (!on)
            {
                g.MouseEnter += delegate { ring.Background = Util.B("#4D32D74B"); ring.BorderBrush = Util.B(Green); };
                g.MouseLeave += delegate { ring.Background = Brushes.Transparent; ring.BorderBrush = Util.B(ringColor); };
            }
            return g;
        }

        // سطر الموعد: أيقونة تقويم + نص (ملوّن للمتأخر واليوم فقط)
        UIElement DueLine(string due, string note, bool hot, string hotColor, double size, bool withNote)
        {
            string color = hot ? hotColor : "#99FFFFFF";
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (due.Length > 0)
                g.Children.Add(new TextBlock { Text = "\uE787", FontFamily = Mdl2, FontSize = size - 1, Foreground = Util.B(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
            var tb = new TextBlock { FontSize = size, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Util.B("#99FFFFFF") };
            if (due.Length > 0)
            {
                var r = new System.Windows.Documents.Run(due) { Foreground = Util.B(color) };
                if (hot) r.FontWeight = FontWeights.Medium;
                tb.Inlines.Add(r);
            }
            if (withNote && !string.IsNullOrEmpty(note))
                tb.Inlines.Add(new System.Windows.Documents.Run((due.Length > 0 ? "  ·  " : "") + note));
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            return g;
        }

        // الملاحظة كاملة: الروابط ومسارات الصور بتنفتح بضغطة
        FrameworkElement FullNote(string content)
        {
            var tb = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Util.B("#CCFFFFFF"), Margin = new Thickness(0, 7, 0, 2), LineHeight = 19 };
            var rx = new Regex(@"https?://\S+|[A-Za-z]:\\[^\r\n]*?\.(?:png|jpe?g|webp|gif|bmp)", RegexOptions.IgnoreCase);
            int pos = 0;
            string text = content.Replace("\r", "").Trim();
            foreach (Match m in rx.Matches(text))
            {
                if (m.Index > pos) tb.Inlines.Add(new System.Windows.Documents.Run(text.Substring(pos, m.Index - pos)));
                string target = m.Value;
                var link = new System.Windows.Documents.Run(target) { Foreground = Util.B("#64B5FF"), TextDecorations = TextDecorations.Underline, Cursor = Cursors.Hand };
                link.MouseLeftButtonUp += delegate(object o, MouseButtonEventArgs e) { Util.Open(target); e.Handled = true; };
                tb.Inlines.Add(link);
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) tb.Inlines.Add(new System.Windows.Documents.Run(text.Substring(pos)));
            return tb;
        }

        // hero = أول مهمة (كبيرة). الباقي بطاقات أصغر
        UIElement Row(TodoTask t, DueInfo info, bool hero)
        {
            bool pend = pendingDone.ContainsKey(t.Id);
            bool hot = info.Level == Lvl.Over || info.Level == Lvl.Today;
            string ring = t.Priority >= 5 || info.Level == Lvl.Urgent ? LvlColor[Lvl.Urgent] : (hot ? LvlColor[info.Level] : "#B3FFFFFF");
            string baseFill = hero ? "#26FFFFFF" : "#17FFFFFF";
            string hoverFill = hero ? "#30FFFFFF" : "#26FFFFFF";
            FrameworkElement linkBtn = null;

            var baseColor = (Color)ColorConverter.ConvertFromString(baseFill);
            var hoverColor = (Color)ColorConverter.ConvertFromString(hoverFill);
            var bg = new SolidColorBrush(baseColor);
            var border = new Border
            {
                CornerRadius = new CornerRadius(hero ? 16 : 11),
                Background = bg,
                BorderBrush = Util.B(hero ? "#29FFFFFF" : "#00FFFFFF"),
                BorderThickness = new Thickness(1),
                Padding = hero ? new Thickness(12, 12, 12, 13) : new Thickness(9, 8, 9, 9),
                Margin = new Thickness(0, 0, 0, hero ? 10 : 5),
                Opacity = pend || t.Temp ? 0.6 : 1,
                ToolTip = t.Temp ? null : "دبل كليك لتغيير الاسم"
            };
            border.MouseEnter += delegate
            {
                bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(hoverColor, TimeSpan.FromMilliseconds(110)));
                if (linkBtn != null) linkBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            };
            border.MouseLeave += delegate
            {
                bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(baseColor, TimeSpan.FromMilliseconds(180)));
                if (linkBtn != null) linkBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            double dot = hero ? 20 : 16;
            var check = CheckDot(pend, ring, dot);
            check.Margin = new Thickness(0, hero ? -2 : -3, 2, 0);
            if (!t.Temp) check.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { ToggleComplete(t); e.Handled = true; };
            grid.Children.Add(check);

            var sp = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
            var title = T(t.Title, hero ? 17 : 13, pend ? "#B3FFFFFF" : "#FFFFFF");
            if (hero) title.FontWeight = FontWeights.Bold;
            if (pend) title.TextDecorations = TextDecorations.Strikethrough;
            sp.Children.Add(title);

            if (!t.Temp)
                border.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
                {
                    if (e.ClickCount != 2 || editing) return;
                    e.Handled = true;
                    editing = true;
                    var box = new TextBox
                    {
                        Style = (Style)Resources["Input"], Text = t.Title, FontSize = title.FontSize, FontWeight = title.FontWeight,
                        Foreground = Brushes.White, CaretBrush = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(-7, -6, -7, -4)
                    };
                    int at = sp.Children.IndexOf(title);
                    sp.Children.RemoveAt(at);
                    sp.Children.Insert(at, box);
                    bool finished = false;
                    Action<bool> finish = save =>
                    {
                        if (finished) return;
                        finished = true;
                        if (save) CommitRename(t, box.Text);
                        else { editing = false; lastSig = null; Render(); }
                    };
                    box.KeyDown += delegate(object o2, KeyEventArgs e2)
                    {
                        if (e2.Key == Key.Enter) { finish(true); e2.Handled = true; }
                        else if (e2.Key == Key.Escape) { finish(false); e2.Handled = true; }
                    };
                    box.LostKeyboardFocus += delegate { finish(true); };
                    box.Loaded += delegate { box.Focus(); box.SelectAll(); };
                };

            if (!pend)
            {
                string due = info.Text;
                if (!string.IsNullOrEmpty(t.RepeatFlag)) due += (due.Length > 0 ? " · " : "") + "يتكرر";
                bool hasNote = !string.IsNullOrEmpty(t.Note) && t.Note != t.Title;
                bool isExp = expanded.Contains(t.Id) && !string.IsNullOrWhiteSpace(t.Content);
                if (due.Length > 0 || (hasNote && !isExp))
                {
                    var line = DueLine(due, t.Note, hot, LvlColor[info.Level], hero ? 12 : 11, !hero && !isExp);
                    ((FrameworkElement)line).Margin = new Thickness(0, hero ? 6 : 3, 0, 0);
                    sp.Children.Add(line);
                }
                if (hero && hasNote && !isExp)
                {
                    var note = T(t.Note, 12, "#B3FFFFFF");
                    note.TextWrapping = TextWrapping.NoWrap;
                    note.TextTrimming = TextTrimming.CharacterEllipsis;
                    note.Margin = new Thickness(0, 4, 0, 0);
                    sp.Children.Add(note);
                }
            }
            if (!pend && expanded.Contains(t.Id) && !string.IsNullOrWhiteSpace(t.Content))
                sp.Children.Add(FullNote(t.Content));
            Action editDue = () =>
            {
                if (editing) return;
                editing = true;
                var host = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
                var field = new Grid();
                var box = new TextBox { Style = (Style)Resources["Input"], FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White };
                var hint = T("17:30  ·  بكرا  ·  10د  ·  2026-10-30 17:30", 11.5, "#80FFFFFF");
                hint.IsHitTestVisible = false;
                hint.VerticalAlignment = VerticalAlignment.Center;
                hint.Margin = new Thickness(9, 0, 9, 0);
                hint.TextWrapping = TextWrapping.NoWrap;
                hint.TextTrimming = TextTrimming.CharacterEllipsis;
                if (t.Due != null) box.Text = t.AllDay ? t.Due.Value.ToString("yyyy-MM-dd", Util.Inv) : t.Due.Value.ToString("yyyy-MM-dd HH:mm", Util.Inv);
                hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                box.TextChanged += delegate { hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
                field.Children.Add(box);
                field.Children.Add(hint);
                host.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = Util.B("#26FFFFFF"), Child = field });

                bool finished = false;
                Action<DateTime?, bool, bool> done = (at, timed, clear) => { if (finished) return; finished = true; CommitDue(t, at, timed, clear); };
                Action cancel = () => { if (finished) return; finished = true; editing = false; lastSig = null; Render(); };

                var chips = new WrapPanel { Margin = new Thickness(-6, 2, 0, 0) };
                chips.Children.Add(Pill("اليوم", () => done(DateTime.Today, false, false)));
                chips.Children.Add(Pill("غداً", () => done(DateTime.Today.AddDays(1), false, false)));
                chips.Children.Add(Pill("بعد أسبوع", () => done(DateTime.Today.AddDays(7), false, false)));
                chips.Children.Add(Pill("بدون موعد", () => done(null, false, true)));
                host.Children.Add(chips);

                box.KeyDown += delegate(object o3, KeyEventArgs e3)
                {
                    if (e3.Key == Key.Escape) { cancel(); e3.Handled = true; return; }
                    if (e3.Key != Key.Enter) return;
                    e3.Handled = true;
                    string v = box.Text.Trim();
                    if (v.Length == 0 || v == "-" || v == "بدون" || v == "حذف") { done(null, false, true); return; }
                    bool timed;
                    var at = ParseWhen(v, out timed);
                    if (at == null) { box.Text = ""; hint.Text = "ما فهمت الموعد، جرّب 17:30 أو 6-10-2026"; return; }
                    done(at, timed, false);
                };
                box.LostKeyboardFocus += delegate { cancel(); };
                sp.Children.Add(host);
                box.Loaded += delegate { box.Focus(); box.SelectAll(); };
            };

            Grid.SetColumn(sp, 1);
            grid.Children.Add(sp);

            if ((t.Link != null || t.Image != null || !t.Temp || !string.IsNullOrWhiteSpace(t.Content)) && !pend)
            {
                var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -4, 0), Opacity = expanded.Contains(t.Id) ? 1 : 0 };
                if (!string.IsNullOrWhiteSpace(t.Content))
                {
                    bool open = expanded.Contains(t.Id);
                    acts.Children.Add(IconButton(open ? "\ue70e" : "\ue70d", open ? "طي الملاحظة" : "الملاحظة كاملة", () =>
                    {
                        if (!expanded.Remove(t.Id)) expanded.Add(t.Id);
                        lastSig = null;
                        Render();
                    }));
                }
                if (!t.Temp) acts.Children.Add(IconButton("\ue787", "الموعد", editDue));
                if (t.Link != null) acts.Children.Add(IconButton("", "فتح الرابط", () => Util.Open(t.Link)));
                if (t.Image != null) acts.Children.Add(IconButton("", "فتح الصورة", () => Util.Open(t.Image)));
                if (!t.Temp) acts.Children.Add(IconButton("", "فتح بـ TickTick", () => Util.Open("https://ticktick.com/webapp/#p/" + t.ProjectId + "/tasks/" + t.Id)));
                linkBtn = acts;
                Grid.SetColumn(acts, 2);
                grid.Children.Add(acts);
            }

            border.Child = grid;

            if (rendered && !t.Temp && !shown.Contains(t.Id))
            {
                double to = border.Opacity;
                var move = new TranslateTransform(0, 10);
                border.RenderTransform = move;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                border.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, to, TimeSpan.FromMilliseconds(200)));
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            }
            return border;
        }

        string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Today.Ticks).Append('|').Append(tasks == null).Append(needAuth).Append(connecting).Append('|').Append(statusText).Append('|');
            if (tasks != null)
                foreach (var t in tasks)
                {
                    var i = GetDue(t);
                    sb.Append(t.Id).Append('~').Append(t.Title).Append('~').Append(t.Note).Append('~').Append(t.Link).Append('~').Append(t.Priority)
                      .Append('~').Append(i.Text).Append('~').Append((int)i.Level).Append('~').Append(t.RepeatFlag).Append('~')
                      .Append(pendingDone.ContainsKey(t.Id)).Append('~').Append(t.Temp).Append(';');
                }
            foreach (var p in st.Pending) sb.Append(p.TaskId).Append(p.At).Append(p.Missed).Append(';');
            sb.Append('|').Append(st.DoneDay).Append(st.DoneCount).Append('|').Append(string.Join(",", expanded));
            return sb.ToString();
        }

        void Render()
        {
            renderDay = DateTime.Today;
            if (editing) return;
            string sig = Signature();
            if (sig == lastSig) return;
            lastSig = sig;
            DateTime now = DateTime.Now;
            titleText.Text = Util.Days[(int)now.DayOfWeek] + "، " + now.Day + " " + Util.Months[now.Month - 1];
            listPanel.Children.Clear();

            RenderStatus();
            RenderAlerts();

            if (tasks == null)
            {
                countText.Text = "";
                if (needAuth)
                {
                    var card = new StackPanel { Margin = new Thickness(4, 2, 4, 10) };
                    card.Children.Add(T("اربط حساب TickTick", 12, "#D9FFFFFF"));
                    var wp = new WrapPanel();
                    wp.Children.Add(Pill(connecting ? "بانتظار المتصفح..." : "ربط", Connect));
                    card.Children.Add(wp);
                    listPanel.Children.Add(card);
                    summary.Text = "اربط حساب TickTick";
                }
                else
                {
                    var l = T("جاري التحميل...", 12, "#99FFFFFF");
                    l.Margin = new Thickness(4, 2, 4, 10);
                    listPanel.Children.Add(l);
                    summary.Text = "جاري التحميل...";
                }
                return;
            }

            var open = tasks
                .Select(t => new { T = t, I = GetDue(t) })
                .OrderBy(x => (int)x.I.Level)
                .ThenBy(x => x.T.Due ?? DateTime.MaxValue)
                .ThenByDescending(x => x.T.Priority)
                .ToList();

            if (open.Count > 0)
            {
                listPanel.Children.Add(Row(open[0].T, open[0].I, true));
                if (open.Count > 1)
                {
                    var next = T("التالي", 11, "#80FFFFFF", true);
                    next.Margin = new Thickness(4, 2, 4, 6);
                    listPanel.Children.Add(next);
                    for (int i = 1; i < open.Count; i++) listPanel.Children.Add(Row(open[i].T, open[i].I, false));
                }
            }
            else
            {
                var e = T("ما في مهام، يعطيك العافية", 12, "#B3FFFFFF");
                e.Margin = new Thickness(4, 2, 4, 10);
                listPanel.Children.Add(e);
            }

            int count = open.Count(x => !pendingDone.ContainsKey(x.T.Id));
            int doneToday = st.DoneDay == DateTime.Today.ToString("yyyy-MM-dd", Util.Inv) ? st.DoneCount : 0;
            countText.Text = (count == 0 ? "ما في مهام" : TasksText(count)) + (doneToday > 0 ? "  ·  أنجزت " + doneToday : "");

            if (open.Count > 0)
            {
                var top = open[0];
                summary.Text = top.T.Title + (string.IsNullOrEmpty(top.I.Text) ? "" : "  ·  " + top.I.Text);
            }
            else summary.Text = "ما في مهام";

            shown.Clear();
            foreach (var t in tasks) shown.Add(t.Id);
            rendered = true;

            string tip = "مهامي - " + count + " مهام";
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void RenderStatus()
        {
            string s = null;
            if (needAuth) s = connecting ? "كمّل الربط بالمتصفح..." : (tasks != null ? "انتهى الربط مع TickTick · اضغط لإعادة الربط" : null);
            else if (statusText != null) s = statusText;
            statusLine.Text = s ?? "";
            statusLine.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void RenderAlerts()
        {
            alertsPanel.Children.Clear();
            if (tasks != null)
            {
                var ids = new HashSet<string>(tasks.Select(t => t.Id));
                ids.Add("__test__");
                if (st.Pending.RemoveAll(p => !ids.Contains(p.TaskId)) > 0) st.Save();
            }

            foreach (var p in st.Pending.ToList())
            {
                var at = Util.FromStamp(p.At) ?? DateTime.Now;
                string color = p.Missed ? "#FF453A" : "#FF9F0A";
                var card = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = Util.B("#33" + color.Substring(1)),
                    BorderBrush = Util.B("#66" + color.Substring(1)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 6, 9),
                    Margin = new Thickness(0, 10, 0, 0)
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = "", FontFamily = Mdl2, FontSize = 14, Foreground = Util.B(color), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top });

                var sp = new StackPanel();
                sp.Children.Add(T(p.Title, 12.5, "#FFFFFF", true));
                sp.Children.Add(T((p.Missed ? "فاتك · كان " : "تذكير · ") + Util.When(at), 11, color));
                var wp = new WrapPanel { Margin = new Thickness(-6, 0, 0, 0) };
                string id = p.TaskId;
                wp.Children.Add(Pill("خلصت", () => { var t = FindTask(id); RemovePending(id); st.Save(); if (t != null) ToggleComplete(t); else Render(); }));
                wp.Children.Add(Pill("10 دقايق", () => Snooze(id, DateTime.Now.AddMinutes(10))));
                wp.Children.Add(Pill("ساعة", () => Snooze(id, DateTime.Now.AddHours(1))));
                wp.Children.Add(Pill("غداً 9 ص", () => Snooze(id, DateTime.Today.AddDays(1).AddHours(9))));
                sp.Children.Add(wp);
                Grid.SetColumn(sp, 1);
                g.Children.Add(sp);

                var x = IconButton("", "إغلاق", () => { RemovePending(id); st.Save(); Render(); });
                x.VerticalAlignment = VerticalAlignment.Top;
                x.Margin = new Thickness(0, -4, 0, 0);
                Grid.SetColumn(x, 2);
                g.Children.Add(x);

                card.Child = g;
                alertsPanel.Children.Add(card);
            }
            SyncAlarm();
        }

        static string AKey(PendingAlert p) { return p.TaskId + "|" + p.At; }

        // المنبه: نافذة + صوت بيظلوا لحد ما تتصرف (خلصت / تأجيل / إسكات)، والصوت بيوقف لحاله بعد 90 ثانية
        void SyncAlarm()
        {
            var active = st.Pending.Where(p => !silenced.Contains(AKey(p))).ToList();
            if (active.Count == 0) { CloseAlarm(); return; }

            if (alarmWin == null)
            {
                try { alarmWin = new AlarmWindow(); }
                catch (Exception ex) { Util.Log("alarm window: " + ex.Message); alarmWin = null; NotifyFallback(); AlarmSound.Start(cfg); return; }
                alarmWin.OnDone = p => { var t = FindTask(p.TaskId); RemovePending(p.TaskId); st.Save(); if (t != null) ToggleComplete(t); else Render(); };
                alarmWin.OnSnooze = (p, at) => Snooze(p.TaskId, at);
                alarmWin.OnDismiss = () =>
                {
                    foreach (var p in st.Pending) silenced.Add(AKey(p));
                    SyncAlarm();
                };
                alarmWin.Show();
            }
            alarmWin.Set(active[0], active.Count - 1);

            bool fresh = false;
            foreach (var p in active) if (rung.Add(AKey(p))) fresh = true;
            if (fresh)
            {
                if (Native.FreeCommitMB() < 600) NotifyFallback();
                AlarmSound.Start(cfg);
                if (alarmStop == null)
                {
                    alarmStop = new DispatcherTimer();
                    alarmStop.Tick += delegate { alarmStop.Stop(); AlarmSound.Stop(); };
                }
                alarmStop.Stop();
                if (cfg.RingSeconds > 0)
                {
                    alarmStop.Interval = TimeSpan.FromSeconds(cfg.RingSeconds);
                    alarmStop.Start();
                }
            }
        }

        // لو الذاكرة خلصت والنافذة ما قدرت تنرسم: إشعار ويندوز العادي (ما بيحتاج رسم WPF)
        DateTime lastFallback = DateTime.MinValue;
        public void NotifyFallback()
        {
            if (st.Pending.Count == 0 || (DateTime.Now - lastFallback).TotalSeconds < 60) return;
            lastFallback = DateTime.Now;
            try
            {
                string txt = string.Join("\n", st.Pending.Take(3).Select(p => p.Title));
                tray.ShowBalloonTip(20000, "تنبيه", txt, Forms.ToolTipIcon.Warning);
            }
            catch (Exception ex) { Util.Log("fallback: " + ex.Message); }
        }

        void CloseAlarm()
        {
            AlarmSound.Stop();
            if (alarmStop != null) alarmStop.Stop();
            Dispatcher.BeginInvoke(new Action(Native.Trim), DispatcherPriority.ApplicationIdle);
            if (alarmWin != null)
            {
                var w = alarmWin;
                alarmWin = null;
                w.OnDone = null; w.OnSnooze = null; w.OnDismiss = null;
                w.FadeOut();
            }
        }

        // تجربة من قائمة الأيقونة
        public void TestAlarm(string title = "تجربة المنبه")
        {
            // تنبيه وهمي: ما بيرتبط بأي مهمة حقيقية، فزر "خلصت" ما بيكمّل شي
            string at = Util.Stamp(DateTime.Now);
            var key = "__test__|" + at;
            silenced.Remove(key); rung.Remove(key);
            RemovePending("__test__");
            st.Pending.Add(new PendingAlert { TaskId = "__test__", Title = title, At = at, Missed = false });
            st.Save();
            lastSig = null;
            Render();
        }

        // ----------------------------------------------------------- الأفعال
        TodoTask FindTask(string id) { return tasks == null ? null : tasks.FirstOrDefault(t => t.Id == id); }

        void RemovePending(string id) { st.Pending.RemoveAll(p => p.TaskId == id); }

        void Snooze(string id, DateTime at)
        {
            var p = st.Pending.FirstOrDefault(x => x.TaskId == id);
            RemovePending(id);
            st.Snoozes.RemoveAll(s => s.TaskId == id);
            st.Snoozes.Add(new SnoozeEntry { TaskId = id, Title = p != null ? p.Title : "", At = Util.Stamp(at) });
            st.Save();
            Render();
        }

        void ToggleComplete(TodoTask t)
        {
            DispatcherTimer tm;
            if (pendingDone.TryGetValue(t.Id, out tm))
            {
                tm.Stop();
                pendingDone.Remove(t.Id);
                Render();
                return;
            }
            // مهلة 3 ثواني للتراجع قبل ما نبعت لـ TickTick
            tm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            tm.Tick += async delegate
            {
                tm.Stop();
                await CommitComplete(t);
            };
            pendingDone[t.Id] = tm;
            tm.Start();
            Render();
        }

        async Task CommitComplete(TodoTask t)
        {
            try
            {
                await api.Complete(t);
                string today = DateTime.Today.ToString("yyyy-MM-dd", Util.Inv);
                if (st.DoneDay != today) { st.DoneDay = today; st.DoneCount = 0; }
                st.DoneCount++;
                pendingDone.Remove(t.Id);
                if (tasks != null) tasks.RemoveAll(x => x.Id == t.Id);
                RemovePending(t.Id);
                st.Snoozes.RemoveAll(s => s.TaskId == t.Id);
                st.Save();
                SaveCache();
                statusText = null;
                if (!string.IsNullOrEmpty(t.RepeatFlag)) { var _ = Refresh(); }
            }
            catch (AuthException) { pendingDone.Remove(t.Id); SetNeedAuth(); }
            catch (Exception ex)
            {
                pendingDone.Remove(t.Id);
                Util.Log("complete: " + ex.Message);
                statusText = "ما زبط، تأكد من النت وجرّب كمان مرة";
            }
            Render();
        }

        // الكيبورد العربي بيكتب أرقام ٠١٢٣ وفواصل عربية: بنحوّلها لأرقام عادية
        static string NormalizeDigits(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660')));
                else if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));
                else if (c == '\u066B' || c == '\u060C' || c == '\u066C') sb.Append('.');
                else if (c == '\u200F' || c == '\u200E' || c == '\u202B' || c == '\u202C') continue;   // علامات الاتجاه
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static DateTime CeilMinute(DateTime t)
        {
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0).AddMinutes(1);
        }

        static DateTime? ResolveDue(string tok)
        {
            tok = tok.Trim();
            DateTime d;
            if (DateTime.TryParseExact(tok, "yyyy-MM-dd", Util.Inv, DateTimeStyles.None, out d)) return d;
            if (tok == "اليوم") return DateTime.Today;
            if (Regex.IsMatch(tok, "^(بكرا|بكره|بكرة|غدا|غداً)$")) return DateTime.Today.AddDays(1);
            var m = Regex.Match(tok, @"^\+(\d{1,3})$");
            if (m.Success) return DateTime.Today.AddDays(int.Parse(m.Groups[1].Value));
            // 6-10-2026 / 6/10/2026 / 6.10.2026 (يوم-شهر-سنة)، و 2026/10/6، و 6-10 (أقرب مرة جاية)
            try
            {
                m = Regex.Match(tok, @"^(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})$");
                if (m.Success) return new DateTime(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value));
                m = Regex.Match(tok, @"^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$");
                if (m.Success) return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                m = Regex.Match(tok, @"^(\d{1,2})[-/.](\d{1,2})$");
                if (m.Success)
                {
                    var d1 = new DateTime(DateTime.Today.Year, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value));
                    return d1 < DateTime.Today ? d1.AddYears(1) : d1;
                }
            }
            catch (ArgumentOutOfRangeException) { }
            return null;
        }

        // @17:30  @10د  @+25د  @2س  @ساعة  @بكرا  @2026-10-08. timed = تنبيه بوقت محدد
        static DateTime? ParseWhen(string tok, out bool timed)
        {
            timed = false;
            tok = NormalizeDigits(tok).Trim();
            var two = tok.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (two.Length == 2)
            {
                var day = ResolveDue(two[0]);
                var hm = Regex.Match(two[1], @"^(\d{1,2}):(\d{2})$");
                if (day == null || !hm.Success) return null;
                int hh = int.Parse(hm.Groups[1].Value), mm2 = int.Parse(hm.Groups[2].Value);
                if (hh > 23 || mm2 > 59) return null;
                timed = true;
                return day.Value.Date.AddHours(hh).AddMinutes(mm2);
            }
            var m = Regex.Match(tok, @"^(\d{1,2}):(\d{2})$");
            if (m.Success)
            {
                int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
                if (h < 24 && mi < 60)
                {
                    var t = DateTime.Today.AddHours(h).AddMinutes(mi);
                    if (t <= DateTime.Now) t = t.AddDays(1);
                    timed = true;
                    return t;
                }
                return null;
            }
            m = Regex.Match(tok, @"^\+?(\d{1,3})(د|دقيقة|دقايق|m|min)$");
            if (m.Success) { timed = true; return CeilMinute(DateTime.Now.AddMinutes(int.Parse(m.Groups[1].Value))); }
            m = Regex.Match(tok, @"^\+?(\d{1,2})?(س|ساعة|ساعات|h)$");
            if (m.Success)
            {
                int n = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1;
                timed = true;
                return CeilMinute(DateTime.Now.AddHours(n));
            }
            return ResolveDue(tok);
        }

        void AddFromBox()
        {
            string text = newBox.Text.Trim();
            if (text.Length == 0) return;
            string title = text;
            DateTime? due = null;
            bool timed = false;
            var m = Regex.Match(text, @"^(.*?)\s*@(\S+)$");
            if (m.Success)
            {
                var d = ParseWhen(m.Groups[2].Value, out timed);
                if (d != null) { due = d; title = m.Groups[1].Value.Trim(); }
            }
            if (title.Length == 0) title = timed ? "تنبيه" : "";
            if (title.Length == 0) return;
            if (needAuth) { Connect(); return; }
            newBox.Text = "";
            var _ = CreateTask(title, due, timed, text);
            if (timed && due != null) Flash("تنبيه " + Util.When(due.Value));
        }

        // timed = تنبيه: بوقت محدد وبيرن المنبه، وبينحفظ بـ TickTick فبيوصل للجوال كمان
        async Task CreateTask(string title, DateTime? due, bool timed, string restoreText)
        {
            if (needAuth) { Connect(); return; }
            var body = new Dictionary<string, object> { { "title", title } };
            if (due != null)
            {
                body["dueDate"] = Util.ToTT(due.Value);
                body["startDate"] = Util.ToTT(due.Value);
                body["isAllDay"] = !timed;
                body["timeZone"] = cfg.TimeZone;
                if (timed) body["reminders"] = new List<string> { "TRIGGER:PT0S" };
            }
            if (tasks == null) tasks = new List<TodoTask>();
            var temp = new TodoTask { Id = "tmp-" + Guid.NewGuid().ToString("N"), Title = title, Due = due, AllDay = !timed, Content = "", Temp = true };
            tasks.Add(temp);
            Render();
            try
            {
                await api.Create(body);
                await Refresh(true);
            }
            catch (AuthException) { tasks.Remove(temp); if (restoreText != null) newBox.Text = restoreText; SetNeedAuth(); }
            catch (Exception ex)
            {
                Util.Log("create: " + ex.Message);
                tasks.Remove(temp);
                if (restoreText != null) newBox.Text = restoreText;
                statusText = "ما انضافت، تأكد من النت";
                Render();
            }
        }

        // رسالة قصيرة مكان سطر العدد
        void Flash(string msg)
        {
            countText.Text = msg;
            if (flashTimer == null)
            {
                flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                flashTimer.Tick += delegate { flashTimer.Stop(); lastSig = null; Render(); };
            }
            flashTimer.Stop();
            flashTimer.Start();
        }

        // تحديد/تغيير/حذف الموعد: بيظهر هون فوراً وبينحفظ بـ TickTick
        async void CommitDue(TodoTask t, DateTime? at, bool timed, bool clear)
        {
            editing = false;
            var body = new Dictionary<string, object> { { "id", t.Id }, { "projectId", t.ProjectId } };
            DateTime? oldDue = t.Due, oldStart = t.Start;
            bool oldAll = t.AllDay;
            if (clear)
            {
                body["dueDate"] = "1970-01-01T00:00:00.000+0000";
                body["startDate"] = "1970-01-01T00:00:00.000+0000";
                body["reminders"] = new List<string>();
                t.Due = null; t.Start = null;
            }
            else
            {
                body["dueDate"] = Util.ToTT(at.Value);
                body["startDate"] = Util.ToTT(at.Value);
                body["isAllDay"] = !timed;
                body["timeZone"] = cfg.TimeZone;
                // التذكيرات نسبية للموعد: لو تغيّر نوعه (يوم كامل / بوقت) أو ما كان في تذكير، بنحط الافتراضي
                if (t.Reminders.Count == 0 || oldAll == timed)
                    body["reminders"] = new List<string> { timed ? "TRIGGER:PT0S" : "TRIGGER:P0DT9H0M0S" };
                t.Due = at; t.Start = at; t.AllDay = !timed;
            }
            lastSig = null;
            Render();
            try
            {
                await api.Update(t.Id, body);
                await Refresh(true);
            }
            catch (AuthException) { t.Due = oldDue; t.Start = oldStart; t.AllDay = oldAll; SetNeedAuth(); }
            catch (Exception ex)
            {
                Util.Log("due: " + ex.Message);
                t.Due = oldDue; t.Start = oldStart; t.AllDay = oldAll;
                statusText = "ما انحفظ الموعد، تأكد من النت";
                lastSig = null;
                Render();
            }
        }

        // الاسم بيتغيّر هون فوراً وبينحفظ بـ TickTick بالخلفية
        async void CommitRename(TodoTask t, string text)
        {
            editing = false;
            text = (text ?? "").Trim();
            if (text.Length == 0 || text == t.Title) { lastSig = null; Render(); return; }
            string old = t.Title;
            t.Title = text;
            if (t.Raw != null) t.Raw["title"] = text;
            lastSig = null;
            Render();
            try
            {
                await api.Update(t.Id, new Dictionary<string, object> { { "id", t.Id }, { "projectId", t.ProjectId }, { "title", text } });
                SaveCache();
            }
            catch (AuthException) { t.Title = old; if (t.Raw != null) t.Raw["title"] = old; SetNeedAuth(); }
            catch (Exception ex)
            {
                Util.Log("rename: " + ex.Message);
                t.Title = old;
                if (t.Raw != null) t.Raw["title"] = old;
                statusText = "ما انحفظ الاسم، تأكد من النت";
                lastSig = null;
                Render();
            }
        }

        void SetNeedAuth()
        {
            api.ClearToken();
            needAuth = true;
            Render();
        }

        // ----------------------------------------------------------- التحديث من TickTick
        async Task Refresh(bool force = false)
        {
            if (refreshing && !force) return;
            refreshing = true;
            lastRefreshTry = DateTime.Now;
            try
            {
                if (!api.HasToken) { needAuth = true; return; }
                var list = await api.FetchAll();
                // المهام يلي لسا بمهلة التراجع بنخليها ظاهرة
                tasks = list;
                needAuth = false;
                lastOk = DateTime.Now;
                statusText = null;
                SaveCache();
            }
            catch (AuthException) { api.ClearToken(); needAuth = true; }
            catch (Exception ex)
            {
                Util.Log("refresh: " + ex.Message);
                statusText = "غير متصل" + (lastOk != null ? " · آخر تحديث " + Util.Clock(lastOk.Value) : "") + " · اضغط للمحاولة";
            }
            finally
            {
                refreshing = false;
                Render();
                CheckAlerts();
                Dispatcher.BeginInvoke(new Action(Native.Trim), DispatcherPriority.ApplicationIdle);
            }
        }

        List<TodoTask> LoadCache()
        {
            try
            {
                string f = Util.P("cache.json");
                if (!File.Exists(f)) return null;
                var list = new List<TodoTask>();
                foreach (var o in Util.Arr(Util.Json.DeserializeObject(File.ReadAllText(f, Encoding.UTF8))))
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null) list.Add(TodoTask.From(d));
                }
                lastOk = File.GetLastWriteTime(f);
                return list;
            }
            catch (Exception ex) { Util.Log("cache read: " + ex.Message); return null; }
        }

        void SaveCache()
        {
            if (tasks == null) return;
            try { Util.WriteAtomic(Util.P("cache.json"), Util.Json.Serialize(tasks.Where(t => !t.Temp).Select(t => t.Raw).ToList())); }
            catch (Exception ex) { Util.Log("cache save: " + ex.Message); }
        }

        // ----------------------------------------------------------- الربط (OAuth)
        class Secret { public string Id, Key; }

        static Secret LoadSecret()
        {
            try
            {
                var d = Util.Json.DeserializeObject(File.ReadAllText(Util.P("ticktick.secret.json"), Encoding.UTF8)) as Dictionary<string, object>;
                var s = new Secret { Id = Util.S(d, "client_id"), Key = Util.S(d, "client_secret") };
                if (string.IsNullOrEmpty(s.Id) || string.IsNullOrEmpty(s.Key)) return null;
                return s;
            }
            catch { return null; }
        }

        static void Reply(HttpListenerContext ctx, bool ok)
        {
            string msg = ok ? "تم الربط، ارجع للويدجت" : "ما زبط الربط، جرّب كمان مرة";
            string html = "<!doctype html><html dir='rtl' lang='ar'><head><meta charset='utf-8'><title>مهامي</title></head>"
                        + "<body style='font-family:Segoe UI,sans-serif;background:#191A1F;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'>"
                        + "<div style='font-size:22px'>" + msg + "</div></body></html>";
            byte[] b = Encoding.UTF8.GetBytes(html);
            try
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.Close();
            }
            catch { }
        }

        async void Connect()
        {
            if (connecting) return;
            var sec = LoadSecret();
            if (sec == null) { statusText = "ملف ticktick.secret.json ناقص"; needAuth = false; Render(); return; }
            connecting = true;
            needAuth = true;
            Render();
            HttpListener l = null;
            bool ok = false;
            try
            {
                l = new HttpListener();
                l.Prefixes.Add("http://localhost:8790/");
                l.Start();
                string state = Guid.NewGuid().ToString("N");
                Util.Open("https://ticktick.com/oauth/authorize?scope=" + Uri.EscapeDataString(Api.Scope)
                        + "&client_id=" + Uri.EscapeDataString(sec.Id)
                        + "&state=" + state
                        + "&redirect_uri=" + Uri.EscapeDataString(Api.Redirect)
                        + "&response_type=code");
                DateTime until = DateTime.Now.AddMinutes(5);
                while (DateTime.Now < until)
                {
                    Task<HttpListenerContext> get = l.GetContextAsync();
                    if (await Task.WhenAny(get, Task.Delay(until - DateTime.Now)) != get) break;
                    var ctx = get.Result;
                    if (ctx.Request.Url.AbsolutePath != "/callback") { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                    string code = ctx.Request.QueryString["code"];
                    if (code != null && ctx.Request.QueryString["state"] == state)
                    {
                        try { await api.Exchange(code, sec.Id, sec.Key); ok = true; }
                        catch (Exception ex) { Util.Log("token exchange: " + ex.Message); }
                    }
                    Reply(ctx, ok);
                    break;
                }
            }
            catch (Exception ex) { Util.Log("connect: " + ex.Message); }
            finally
            {
                if (l != null) { try { l.Close(); } catch { } }
                connecting = false;
            }
            if (ok)
            {
                needAuth = false;
                statusText = null;
                await Refresh(true);
            }
            else Render();
        }

        // ----------------------------------------------------------- التنبيهات
        void CheckAlerts()
        {
            if (tasks == null) return;
            DateTime now = DateTime.Now;
            var fired = new List<PendingAlert>();
            var alerted = new HashSet<string>(st.Alerted);
            bool changed = false;

            foreach (var t in tasks)
            {
                if (t.Temp || pendingDone.ContainsKey(t.Id)) continue;
                foreach (var a in t.Alarms())
                {
                    if (a > now || a < now.AddDays(-7)) continue;
                    string key = t.Id + "|" + Util.Stamp(a);
                    if (alerted.Contains(key)) continue;
                    alerted.Add(key);
                    st.Alerted.Add(key);
                    changed = true;
                    if (!st.Initialized) continue;  // أول تشغيل: ما بنطلّع تنبيهات قديمة
                    var p = new PendingAlert { TaskId = t.Id, Title = t.Title, At = Util.Stamp(a), Missed = (now - a).TotalMinutes > 3 };
                    fired.RemoveAll(x => x.TaskId == t.Id);
                    fired.Add(p);
                }
            }

            foreach (var s in st.Snoozes.ToList())
            {
                var at = Util.FromStamp(s.At);
                if (at == null) { st.Snoozes.Remove(s); changed = true; continue; }
                if (at.Value > now) continue;
                st.Snoozes.Remove(s);
                changed = true;
                var t = FindTask(s.TaskId);
                if (t == null) continue;
                fired.RemoveAll(x => x.TaskId == t.Id);
                fired.Add(new PendingAlert { TaskId = t.Id, Title = t.Title, At = s.At, Missed = (now - at.Value).TotalMinutes > 3 });
            }

            if (!st.Initialized) { st.Initialized = true; changed = true; }
            foreach (var p in fired) { RemovePending(p.TaskId); st.Pending.Add(p); }
            if (changed || fired.Count > 0) st.Save();
            if (fired.Count == 0) return;

            if (st.Collapsed) { st.Collapsed = false; ApplyCollapse(); }
            Render();
            if (!IsVisible) Show();

        }

        // ----------------------------------------------------------- النافذة وسطح المكتب
        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            long ex = Native.GetLong(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetLong(hwnd, Native.GWL_EXSTYLE, new IntPtr((ex | Native.WS_EX_TOOLWINDOW) & ~(long)Native.WS_EX_APPWINDOW));
            AttachToDesktop();
            PlaceWindow();
            ApplyGlass();
        }

        // ملكية النافذة لـ Progman بتخليها ظاهرة مع Win+D وثابتة على سطح المكتب
        // النافذة بتصير ابن لنافذة سطح المكتب نفسها (يلي فيها الأيقونات)،
        // فبتضل جزء من الخلفية: تحت كل البرامج، وبتبين مع Win+D
        void AttachToDesktop()
        {
            IntPtr host = Native.DesktopHost();
            if (host == IntPtr.Zero)
            {
                if (attachRetry == null)
                {
                    attachRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    int tries = 0;
                    attachRetry.Tick += delegate
                    {
                        if (Native.DesktopHost() != IntPtr.Zero || ++tries > 30) { attachRetry.Stop(); attachRetry = null; AttachToDesktop(); }
                    };
                    attachRetry.Start();
                }
                return;
            }
            if (host == desktopHost && Native.GetParent(hwnd) == host) return;
            desktopHost = host;
            long style = Native.GetLong(hwnd, Native.GWL_STYLE).ToInt64();
            style = (style & ~(long)Native.WS_POPUP) | Native.WS_CHILD;
            Native.SetLong(hwnd, Native.GWL_STYLE, new IntPtr(style));
            Native.SetParent(hwnd, host);
            ApplyZ();
        }

        IntPtr desktopHost;

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            return IntPtr.Zero;
        }

        // فوق أيقونات سطح المكتب بس (بين إخوتها جوّا سطح المكتب)
        void ApplyZ()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // إظهار = عرض سطح المكتب (زي Win+D)
        public void Peek()
        {
            if (!IsVisible) Show();
            try
            {
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                shell.GetType().InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            }
            catch (Exception ex) { Util.Log("show desktop: " + ex.Message); }
        }

        Matrix ToDevice()
        {
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice;
            return Matrix.Identity;
        }

        void PlaceWindow()
        {
            if (st.Left != null && st.Top != null) { Left = st.Left.Value; Top = st.Top.Value; }
            else
            {
                var m = ToDevice();
                var wa = Forms.Screen.PrimaryScreen.WorkingArea;
                Left = wa.Right / m.M11 - Width - 4;
                Top = wa.Top / m.M22 + 4;
            }
            EnsureOnScreen();
        }

        // لو تغيّرت الشاشات/الدقة والنافذة طلعت برا، بنرجعها
        void EnsureOnScreen()
        {
            if (hwnd == IntPtr.Zero) return;
            var m = ToDevice();
            double w = ActualWidth > 0 ? ActualWidth : Width;
            var head = new Drawing.Rectangle((int)(Left * m.M11), (int)(Top * m.M22), (int)(w * m.M11), (int)(60 * m.M22));
            foreach (var s in Forms.Screen.AllScreens)
            {
                var i = Drawing.Rectangle.Intersect(s.WorkingArea, head);
                if (i.Width >= 100 * m.M11 && i.Height >= 30 * m.M22) return;
            }
            var wa = Forms.Screen.PrimaryScreen.WorkingArea;
            Left = wa.Right / m.M11 - w - 4;
            Top = wa.Top / m.M22 + 4;
            st.Left = Left; st.Top = Top; st.Save();
        }

        // ----------------------------------------------------------- لوحات التنبيه السريع والإعدادات
        void SetActive(Button b, bool on)
        {
            if (on) b.Foreground = Util.B(Green); else b.ClearValue(Control.ForegroundProperty);
        }

        // --panel=quick|settings|list من سطر الأوامر (للاختبار والاختصارات)
        public void OpenPanel(string name)
        {
            if (name.StartsWith("due|"))
            {
                var dp = name.Substring(4).Split(new[] { '|' }, 2);
                var dt = dp.Length == 2 ? FindTask(dp[0]) : null;
                if (dt == null) { Util.Log("due-cli: task not found"); return; }
                string dv = dp[1].Trim();
                if (dv.Length == 0 || dv == "-") { CommitDue(dt, null, false, true); return; }
                bool dtimed;
                var dat = ParseWhen(dv, out dtimed);
                Util.Log("due-cli: '" + dv + "' -> " + (dat == null ? "null" : dat.Value.ToString("s") + " timed=" + dtimed));
                if (dat != null) CommitDue(dt, dat, dtimed, false);
                return;
            }
            if (name.StartsWith("ring|")) { TestAlarm(name.Substring(5)); return; }
            if (name.StartsWith("alarm|")) { QuickFromText(name.Substring(6)); return; }
            if (name.StartsWith("rename|"))
            {
                var parts = name.Substring(7).Split(new[] { '|' }, 2);
                var rt = parts.Length == 2 ? FindTask(parts[0]) : null;
                if (rt != null) CommitRename(rt, parts[1]);
                return;
            }
            Mode want = name == "quick" ? Mode.Quick : name == "settings" ? Mode.Settings : Mode.List;
            if (want == Mode.List) { if (mode != Mode.List) SetMode(mode); return; }
            if (mode != want) SetMode(want);
        }

        // --alarm=17:30|اسم  أو  --alarm=10د|اسم
        void QuickFromText(string spec)
        {
            int i = spec.IndexOf('|');
            string when = i < 0 ? spec : spec.Substring(0, i);
            string title = i < 0 ? "" : spec.Substring(i + 1).Trim();
            bool timed;
            var at = ParseWhen(when, out timed);
            if (at == null || !timed) { Flash("وقت غير مفهوم"); return; }
            var _ = CreateTask(title.Length == 0 ? "تنبيه" : title, at, true, null);
            Flash("تنبيه " + Util.When(at.Value));
        }

        void SetMode(Mode m)
        {
            mode = mode == m ? Mode.List : m;
            bool list = mode == Mode.List;
            listScroll.Visibility = hairline.Visibility = addGrid.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            panelHost.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
            panelHost.Children.Clear();
            if (mode == Mode.Quick) BuildQuickPanel();
            else if (mode == Mode.Settings) BuildSettingsPanel();
            SetActive(btnQuick, mode == Mode.Quick);
            SetActive(btnSettings, mode == Mode.Settings);
            if (st.Collapsed && !list) { st.Collapsed = false; ApplyCollapse(); st.Save(); }
            panelHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        Grid FieldCard(string hint, out TextBox box)
        {
            var tb = new TextBox { Style = (Style)Resources["Input"] };
            var h = T(hint, 12, "#80FFFFFF");
            h.IsHitTestVisible = false;
            h.VerticalAlignment = VerticalAlignment.Center;
            h.Margin = new Thickness(9, 0, 9, 0);
            h.TextWrapping = TextWrapping.NoWrap;
            tb.TextChanged += delegate { h.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            var g = new Grid();
            g.Children.Add(tb);
            g.Children.Add(h);
            box = tb;
            return g;
        }

        void BuildQuickPanel()
        {
            var head = T("تنبيه سريع", 13, "#FFFFFF", true);
            head.Margin = new Thickness(3, 0, 3, 8);
            panelHost.Children.Add(head);

            TextBox titleBox, timeBox;
            var titleCard = new Border { CornerRadius = new CornerRadius(10), Background = Util.B("#17FFFFFF"), Child = FieldCard("اسم التنبيه (اختياري)", out titleBox) };
            panelHost.Children.Add(titleCard);

            Action<DateTime> go = at =>
            {
                string t = titleBox.Text.Trim();
                SetMode(Mode.List);
                var _ = CreateTask(t.Length == 0 ? "تنبيه" : t, at, true, null);
                Flash("تنبيه " + Util.When(at));
            };

            var wrap = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
            int[] mins = { 5, 10, 15, 30, 60, 120 };
            string[] names = { "5 دقايق", "10 دقايق", "15 دقيقة", "30 دقيقة", "ساعة", "ساعتين" };
            for (int i = 0; i < mins.Length; i++)
            {
                int mm = mins[i];
                wrap.Children.Add(Pill(names[i], () => go(CeilMinute(DateTime.Now.AddMinutes(mm)))));
            }
            wrap.Children.Add(Pill("غداً 9 ص", () => go(DateTime.Today.AddDays(1).AddHours(9))));
            panelHost.Children.Add(wrap);

            var timeCard = new Border { CornerRadius = new CornerRadius(10), Background = Util.B("#17FFFFFF"), Margin = new Thickness(0, 10, 0, 0), Child = FieldCard("أو وقت محدد: 17:30  أو  25د  ثم Enter", out timeBox) };
            panelHost.Children.Add(timeCard);
            timeBox.KeyDown += delegate(object s2, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    bool timed;
                    var at = ParseWhen(timeBox.Text, out timed);
                    if (at != null && timed) go(at.Value);
                    else Flash("وقت غير مفهوم");
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape) SetMode(Mode.List);
            };
            titleBox.KeyDown += delegate(object s2, KeyEventArgs e) { if (e.Key == Key.Escape) SetMode(Mode.List); };
            Dispatcher.BeginInvoke(new Action(() => { titleBox.Focus(); }), DispatcherPriority.Background);
        }

        FrameworkElement Seg(string[] opts, int sel, Action<int> pick)
        {
            var host = new Border { CornerRadius = new CornerRadius(9), Background = Util.B("#33000000"), Padding = new Thickness(2) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var items = new List<Border>();
            var texts = new List<TextBlock>();
            Action<int> paint = k =>
            {
                for (int j = 0; j < items.Count; j++)
                {
                    items[j].Background = j == k ? Util.B("#40FFFFFF") : Brushes.Transparent;
                    texts[j].Opacity = j == k ? 1 : 0.65;
                }
            };
            for (int i = 0; i < opts.Length; i++)
            {
                int idx = i;
                var t = T(opts[i], 11.5, "#FFFFFF");
                t.TextWrapping = TextWrapping.NoWrap;
                var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4, 9, 5), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = t };
                items.Add(b);
                texts.Add(t);
                b.MouseLeftButtonUp += delegate { paint(idx); pick(idx); };
                sp.Children.Add(b);
            }
            paint(sel);
            host.Child = sp;
            return host;
        }

        FrameworkElement Toggle(bool on, Action<bool> change)
        {
            bool v = on;
            var track = new Border { Width = 42, Height = 24, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, FlowDirection = FlowDirection.LeftToRight };
            var knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.White, Margin = new Thickness(2) };
            track.Child = knob;
            Action paint = () =>
            {
                track.Background = Util.B(v ? Green : "#4DFFFFFF");
                knob.HorizontalAlignment = v ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            };
            paint();
            track.MouseLeftButtonUp += delegate { v = !v; paint(); change(v); };
            return track;
        }

        void AddRow(string label, FrameworkElement ctl)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = T(label, 12.5, "#E6FFFFFF");
            l.VerticalAlignment = VerticalAlignment.Center;
            l.TextWrapping = TextWrapping.NoWrap;
            ctl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ctl, 1);
            g.Children.Add(l);
            g.Children.Add(ctl);
            panelHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = Util.B("#17FFFFFF"), Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 0, 5), Child = g });
        }

        static bool TaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN AnasTickWidget") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var pr = Process.Start(psi)) { pr.WaitForExit(4000); return pr.HasExited && pr.ExitCode == 0; }
            }
            catch { return false; }
        }

        void SetAutostart(bool on)
        {
            Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Util.P("install.ps1") + "\"" + (on ? "" : " -Remove")) { CreateNoWindow = true, UseShellExecute = false };
                    using (var pr = Process.Start(psi)) pr.WaitForExit(20000);
                }
                catch (Exception ex) { Util.Log("autostart: " + ex.Message); }
            });
        }

        // معاينة قصيرة للصوت (ما بتشتغل لو في منبه حقيقي شغال)
        void PreviewSound()
        {
            if (alarmWin != null) return;
            AlarmSound.Stop();
            AlarmSound.Start(cfg);
            if (previewTimer == null)
            {
                previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
                previewTimer.Tick += delegate { previewTimer.Stop(); if (alarmWin == null) AlarmSound.Stop(); };
            }
            previewTimer.Stop();
            previewTimer.Start();
        }

        void BuildSettingsPanel()
        {
            var head = T("الإعدادات", 13, "#FFFFFF", true);
            head.Margin = new Thickness(3, 0, 3, 8);
            panelHost.Children.Add(head);

            string[] tones = { "bell", "digital", "soft" };
            double[] vols = { 0.35, 0.65, 1.0 };
            int[] rings = { 30, 90, 0 };
            double[] glass = { 0.3, 0.5, 0.75 };
            Func<double[], double, int> nearest = (arr, v) =>
            {
                int best = 0;
                for (int i = 1; i < arr.Length; i++) if (Math.Abs(arr[i] - v) < Math.Abs(arr[best] - v)) best = i;
                return best;
            };

            AddRow("الصوت", Toggle(cfg.SoundOn, v => { cfg.SoundOn = v; cfg.Save(); if (v) PreviewSound(); else AlarmSound.Stop(); }));
            AddRow("النغمة", Seg(new[] { "جرس", "رقمي", "ناعم" }, Math.Max(0, Array.IndexOf(tones, cfg.Tone)), i => { cfg.Tone = tones[i]; cfg.Save(); PreviewSound(); }));
            AddRow("مستوى الصوت", Seg(new[] { "هادي", "متوسط", "عالي" }, nearest(vols, cfg.Volume), i => { cfg.Volume = vols[i]; cfg.Save(); PreviewSound(); }));
            AddRow("مدة الرنين", Seg(new[] { "30 ث", "90 ث", "مفتوح" }, Math.Max(0, Array.IndexOf(rings, cfg.RingSeconds)), i => { cfg.RingSeconds = rings[i]; cfg.Save(); }));
            AddRow("الشكل", Seg(new[] { "عادي", "زجاج" }, cfg.Style == "glass" ? 1 : 0, i => { cfg.Style = i == 1 ? "glass" : "mica"; cfg.Save(); ApplyStyle(); }));
            AddRow("الشفافية", Seg(new[] { "خفيف", "متوسط", "غامق" }, nearest(glass, cfg.Opacity), i =>
            {
                cfg.Opacity = glass[i];
                cfg.Save();
                tint.Color = Color.FromArgb((byte)Math.Round(cfg.Opacity * 255), 0x0A, 0x0C, 0x14);
                if (cfg.Style != "glass") SetMica();
            }));
            AddRow("التشغيل مع الجهاز", Toggle(TaskExists(), v => SetAutostart(v)));

            var wrap = new WrapPanel { Margin = new Thickness(-6, 2, 0, 0) };
            wrap.Children.Add(Pill("تحديث", () => { SetMode(Mode.List); var _ = Refresh(true); }));
            wrap.Children.Add(Pill("فتح TickTick", () => Util.Open("https://ticktick.com/webapp")));
            wrap.Children.Add(Pill("تجربة المنبه", () => { SetMode(Mode.List); TestAlarm(); }));
            wrap.Children.Add(Pill("إعادة الربط", () => { SetMode(Mode.List); api.ClearToken(); needAuth = true; Render(); Connect(); }));
            wrap.Children.Add(Pill("المكان الافتراضي", () => { st.Left = null; st.Top = null; PlaceWindow(); st.Save(); }));
            wrap.Children.Add(Pill("خروج", ExitApp));
            panelHost.Children.Add(wrap);
        }

        // الزجاج: الجزء من الخلفية المموّهة يلي تحت اللوح بالضبط
        // عادي = لون واحد شبه معتم (خفيف جداً، مثل بطاقة الطقس). زجاج = خلفية مموّهة (أثقل)
        void SetMica()
        {
            byte a = (byte)Math.Round(Math.Min(0.97, 0.7 + cfg.Opacity * 0.4) * 255);
            var c = Color.FromArgb(a, 0x2C, 0x2A, 0x2A);
            if (micaBrush == null) micaBrush = new SolidColorBrush(c); else micaBrush.Color = c;
            if (!ReferenceEquals(rootBorder.Background, micaBrush)) rootBorder.Background = micaBrush;
        }

        void ApplyStyle()
        {
            bool glass = cfg.Style == "glass";
            tintLayer.Visibility = sheenLayer.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
            rootBorder.CornerRadius = new CornerRadius(glass ? 22 : 12);
            rootBorder.Margin = new Thickness(glass ? 14 : 8);
            rootBorder.BorderBrush = Util.B(glass ? "#33FFFFFF" : "#1AFFFFFF");
            rootBorder.Effect = glass ? new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Opacity = 0.3, Color = Colors.Black } : null;
            if (glass) { if (!Glass.Loaded) Glass.Load(); }
            else
            {
                Glass.Unload();
                glassBrush = null;
                Dispatcher.BeginInvoke(new Action(Native.Trim), DispatcherPriority.ApplicationIdle);
            }
            ApplyGlass();
        }

        void ApplyGlass()
        {
            if (rootBorder == null) return;
            if (cfg.Style != "glass") { SetMica(); return; }
            if (hwnd == IntPtr.Zero) return;
            var m = ToDevice();
            double mg = rootBorder.Margin.Left;
            double w = ActualWidth - 2 * mg, h = ActualHeight - 2 * mg;
            if (w <= 1 || h <= 1) return;
            if (glassBrush == null || glassVer != Glass.Version) { glassBrush = Glass.Create(); glassVer = Glass.Version; }
            Brush want = FallbackGlass;
            if (glassBrush != null && Glass.Place(glassBrush, new Rect((Left + mg) * m.M11, (Top + mg) * m.M22, w * m.M11, h * m.M22))) want = glassBrush;
            if (!ReferenceEquals(rootBorder.Background, want)) rootBorder.Background = want;
        }

        void ApplyCollapse()
        {
            body.Visibility = st.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            summary.Visibility = st.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            (st.Collapsed ? (UIElement)summary : body).BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        void ToggleCollapse()
        {
            st.Collapsed = !st.Collapsed;
            ApplyCollapse();
            st.Save();
        }

        // ----------------------------------------------------------- المؤقت والنوم
        void OnTick()
        {
            DateTime now = DateTime.Now;
            if (lastTick != default(DateTime) && (now - lastTick).TotalSeconds > 120) OnResume();
            lastTick = now;
            tickCount++;
            AttachToDesktop();   // Explorer بينقل سطح المكتب بين نوافذ بعد Win+D، فبنتأكد إنه لسا الأب الصح
            if (DateTime.Today != renderDay || tickCount % 3 == 0) Render();
            CheckAlerts();
            if ((now - lastRefreshTry).TotalMinutes >= cfg.RefreshMinutes && !connecting) { var _ = Refresh(); }
        }

        void OnResume()
        {
            if (cfg.Style == "glass") Glass.Load();
            EnsureOnScreen();
            ApplyGlass();
            AttachToDesktop();
            Render();
            CheckAlerts();
            // النت بياخد كم ثانية ليرجع بعد النوم
            lastRefreshTry = DateTime.Now.AddMinutes(-cfg.RefreshMinutes).AddSeconds(15);
        }

        void OnPowerModeChanged(object s, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                Dispatcher.BeginInvoke(new Action(() => { lastTick = DateTime.Now; OnResume(); }));
        }

        void OnDisplayChanged(object s, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => { if (cfg.Style == "glass") Glass.Load(); EnsureOnScreen(); ApplyGlass(); }), DispatcherPriority.Background);
        }

        // ----------------------------------------------------------- الخروج
        void Cleanup()
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            CloseAlarm();
            try { timer.Stop(); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
        }

        void ExitApp()
        {
            exiting = true;
            st.UserExited = true;
            st.Save();
            Cleanup();
            Close();
            Application.Current.Shutdown();
        }

        // لو Explorer رجع اشتغل، النافذة بتنمسح مع Progman، فبنعيد تشغيل الأداة
        void OnClosed(object sender, EventArgs e)
        {
            if (exiting) return;
            Util.Log("window closed unexpectedly, restarting");
            st.Save();
            Cleanup();
            try { Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--restart") { UseShellExecute = false }); }
            catch (Exception ex) { Util.Log("restart: " + ex.Message); }
            Application.Current.Shutdown();
        }
    }

    // -----------------------------------------------------------------
    //  التشغيل
    // -----------------------------------------------------------------
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool auto = args.Contains("--auto");
            bool restart = args.Contains("--restart");
            bool testAlarm = args.Contains("--test-alarm");
            string panelArg = args.FirstOrDefault(a => a.StartsWith("--panel="));
            string alarmArg = args.FirstOrDefault(a => a.StartsWith("--alarm="));
            string ringArg = args.FirstOrDefault(a => a.StartsWith("--ring="));
            string dueArg = args.FirstOrDefault(a => a.StartsWith("--due="));
            string renameArg = args.FirstOrDefault(a => a.StartsWith("--rename="));

            bool created;
            var mutex = new Mutex(true, @"Local\AnasTickWidget", out created);
            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AnasTickWidget.Show");
            var testEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AnasTickWidget.Test");
            var panelEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AnasTickWidget.Panel");
            if (!created && restart)
            {
                try { created = mutex.WaitOne(15000); }
                catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                if (dueArg != null) { try { File.WriteAllText(Util.P("panel.req"), "due|" + dueArg.Substring(6), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (ringArg != null) { try { File.WriteAllText(Util.P("panel.req"), "ring|" + ringArg.Substring(7), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (renameArg != null) { try { File.WriteAllText(Util.P("panel.req"), "rename|" + renameArg.Substring(9), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (alarmArg != null) { try { File.WriteAllText(Util.P("panel.req"), "alarm|" + alarmArg.Substring(8), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (panelArg != null) { try { File.WriteAllText(Util.P("panel.req"), panelArg.Substring(8)); } catch { } panelEvt.Set(); }
                else if (testAlarm) testEvt.Set();       // تجربة المنبه على النسخة الشغالة
                else if (!auto) showEvt.Set();      // تشغيل يدوي والأداة شغالة: بس نظهرها
                return 0;
            }

            var state = WidgetState.Load();
            state.UserExited = false;
            state.Save();

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Widget w = null;
            DateTime lastOom = DateTime.MinValue;
            app.DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;
                if (e.Exception is OutOfMemoryException)
                {
                    if ((DateTime.Now - lastOom).TotalSeconds > 60) { lastOom = DateTime.Now; Util.Log("out of memory (system), free commit " + Native.FreeCommitMB() + " MB"); }
                    if (w != null) w.NotifyFallback();
                    return;
                }
                Util.Log(e.Exception.ToString());
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Util.Log("fatal: " + e.ExceptionObject);

            app.Startup += delegate
            {
                try { w = new Widget(state, Settings.Load()); }
                catch (Exception ex) { Util.Log("startup failed: " + ex); throw; }
                ThreadPool.RegisterWaitForSingleObject(showEvt, delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(() => { if (w != null) w.Peek(); }));
                }, null, -1, false);
                ThreadPool.RegisterWaitForSingleObject(panelEvt, delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        string req = "";
                        try { req = File.ReadAllText(Util.P("panel.req"), Encoding.UTF8).Trim(); File.Delete(Util.P("panel.req")); } catch { }
                        if (w != null) w.OpenPanel(req);
                    }));
                }, null, -1, false);
                ThreadPool.RegisterWaitForSingleObject(testEvt, delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(() => { if (w != null) w.TestAlarm(); }));
                }, null, -1, false);
            };
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }
    }
}
