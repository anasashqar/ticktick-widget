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
using System.Windows.Media.Imaging;
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

        // ملفات البيانات (إعدادات، حالة، توكن، كاش، سجل). افتراضياً بجانب الـ exe،
        // ومع --data=<مجلد> بتروح لمجلد منفصل (للاختبار بدون لمس بياناتك الحقيقية)
        public static string DataDir = Dir;
        public static bool Isolated;
        public static string D(string name) { return Path.Combine(DataDir, name); }
        public static string RestartArgs { get { return "--restart" + (Isolated ? " \"--data=" + DataDir + "\"" : ""); } }

        // لاحقة فريدة لكل مجلد بيانات (أسماء الـ mutex والأحداث)، فنسخة الاختبار ما بتتصادم مع الحقيقية
        public static string InstanceSuffix()
        {
            if (!Isolated) return "";
            uint h = 2166136261;
            foreach (char c in DataDir.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return "." + h.ToString("x");
        }

        static readonly object logLock = new object();
        static readonly Dictionary<string, DateTime> logSeen = new Dictionary<string, DateTime>();

        // نفس الرسالة ما بتنكتب أكتر من مرة كل 15 دقيقة، والملف بيتدوّر عند 256KB (انقطاع النت كان بيكبّره بلا حدود)
        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    DateTime last, now = DateTime.Now;
                    if (logSeen.TryGetValue(msg, out last) && (now - last).TotalMinutes < 15) return;
                    if (logSeen.Count > 200) logSeen.Clear();
                    logSeen[msg] = now;
                    string f = D("error.log");
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string old = f + ".old";
                        try { if (File.Exists(old)) File.Delete(old); File.Move(f, old); } catch { }
                    }
                    File.AppendAllText(f, "[" + now.ToString("s", Inv) + "] " + msg + "\r\n", new UTF8Encoding(false));
                }
            }
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
        public bool AutoUpdate = true;
        public string Sort = "due";                                  // due | priority | list
        public string Hotkey = "Ctrl+Shift+U";                        // اختصار عام: يفتح الإضافة (أو يركّز المنبه)، فاضي = معطّل
        public string AddProject = "";                               // القائمة الافتراضية للإضافة ("" = الوارد)
        public List<string> HiddenProjects = new List<string>();      // قوائم مخفية (ما بتنجلب ولا بتنبّه)
        public int RingSeconds = 90;      // 0 = لحد ما تتصرف

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                string f = Util.D("settings.json");
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
                if ((v = Util.S(d, "autoUpdate")) != null) s.AutoUpdate = v == "True" || v == "true";
                if ((v = Util.S(d, "sort")) != null && (v == "due" || v == "priority" || v == "list")) s.Sort = v;
                if ((v = Util.S(d, "hotkey")) != null) s.Hotkey = v.Trim();
                if ((v = Util.S(d, "addProject")) != null) s.AddProject = v;
                object hp;
                if (d.TryGetValue("hiddenProjects", out hp))
                    foreach (var hv in Util.Arr(hp)) if (hv != null && hv.ToString().Length > 0) s.HiddenProjects.Add(hv.ToString());
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
                    { "maxListHeight", MaxListHeight }, { "sound", SoundOn }, { "tone", Tone }, { "volume", Volume }, { "ringSeconds", RingSeconds }, { "autoUpdate", AutoUpdate }, { "sort", Sort }, { "hiddenProjects", HiddenProjects }, { "hotkey", Hotkey }, { "addProject", AddProject }
                };
                Util.WriteAtomic(Util.D("settings.json"), Util.Json.Serialize(d));
            }
            catch (Exception ex) { Util.Log("settings save: " + ex.Message); }
        }
    }

    // -----------------------------------------------------------------
    //  التحديث الذاتي من GitHub Releases (المستودع لازم يكون عام)
    // -----------------------------------------------------------------
    static class Updater
    {
        public const string Current = "0.0.0";   // build.yml بيبدّلها برقم الإصدار
        const string Repo = "anasashqar/ticktick-widget";
        public static readonly string ExePath = Process.GetCurrentProcess().MainModule.FileName;

        public class Info { public string Tag, ExeUrl, ShaUrl; }

        static byte[] Download(string url, int timeoutMs, string accept)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "TickWidget/" + Current;
            req.Accept = accept;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 50 * 1024 * 1024) throw new IOException("update too large");
                }
                return ms.ToArray();
            }
        }

        // null = ما في نسخة أحدث. بيرمي استثناء لو النت واقع أو المستودع مش عام
        public static Info FindNewer()
        {
            string json = Encoding.UTF8.GetString(Download("https://api.github.com/repos/" + Repo + "/releases/latest", 15000, "application/vnd.github+json"));
            var d = Util.Json.DeserializeObject(json) as Dictionary<string, object>;
            string tag = Util.S(d, "tag_name");
            Version latest, cur;
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out latest)) return null;
            if (!Version.TryParse(Current, out cur)) cur = new Version(0, 0, 0);
            if (latest <= cur) return null;
            var info = new Info { Tag = tag };
            foreach (var o in Util.Arr(d["assets"]))
            {
                var a = o as Dictionary<string, object>;
                string name = Util.S(a, "name"), url = Util.S(a, "browser_download_url");
                if (name == "TickWidget.exe") info.ExeUrl = url;
                else if (name == "TickWidget.exe.sha256") info.ShaUrl = url;
            }
            return info.ExeUrl != null && info.ShaUrl != null ? info : null;
        }

        // تنزيل + تحقق (sha256 وترويسة exe) إلى TickWidget.exe.new
        public static string Stage(Info u)
        {
            byte[] data = Download(u.ExeUrl, 180000, "application/octet-stream");
            string shaText = Encoding.UTF8.GetString(Download(u.ShaUrl, 15000, "application/octet-stream"));
            var m = Regex.Match(shaText, "[0-9a-fA-F]{64}");
            if (!m.Success) throw new IOException("no checksum");
            string got;
            using (var sha = SHA256.Create()) got = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            if (!string.Equals(got, m.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("checksum mismatch");
            if (data.Length < 100000 || data[0] != 0x4D || data[1] != 0x5A) throw new IOException("not an exe");
            string tmp = ExePath + ".new";
            File.WriteAllBytes(tmp, data);
            return tmp;
        }

        // ويندوز بيسمح بإعادة تسمية exe شغّال: القديم ← .old والجديد مكانه
        public static void Swap(string tmp)
        {
            string old = ExePath + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(ExePath, old);
            try { File.Move(tmp, ExePath); }
            catch { try { File.Move(old, ExePath); } catch { } throw; }
        }

        public static void Cleanup()
        {
            try { File.Delete(ExePath + ".old"); } catch { }
            try { File.Delete(ExePath + ".new"); } catch { }
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
        public bool Locked { get; set; }       // قفل المكان والحجم
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
                string f = Util.D("state.json");
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
            try { Util.WriteAtomic(Util.D("state.json"), Util.Json.Serialize(this)); }
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
    class ProjInfo { public string Id, Name, Color; }

    class AuthException : Exception { }
    class NetException : Exception { public NetException(string m) : base(m) { } }   // ما في رد من السيرفر (DNS/اتصال/مهلة)

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
                string f = Util.D("token.dat");
                if (File.Exists(f))
                    token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { Util.Log("token read: " + ex.Message); }
        }

        void SaveToken(string t)
        {
            token = t;
            File.WriteAllBytes(Util.D("token.dat"), ProtectedData.Protect(Encoding.UTF8.GetBytes(t), null, DataProtectionScope.CurrentUser));
        }

        public void ClearToken()
        {
            token = null;
            try { File.Delete(Util.D("token.dat")); } catch { }
        }

        static async Task<string> Http(string method, string url, string auth, string contentType, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "AnasTickWidget/1.0";
            req.Accept = "application/json";
            if (auth != null) req.Headers[HttpRequestHeader.Authorization] = auth;
            Task<string> work = Run(req, contentType, body);
            using (var cts = new CancellationTokenSource())
            {
                // المؤقّت بينلغى لما الطلب يخلص (قبل كان كل طلب يخلّي Task.Delay شغّال 25 ثانية)
                if (await Task.WhenAny(work, Task.Delay(25000, cts.Token)) != work)
                {
                    req.Abort();
                    throw new TimeoutException("timeout " + url);
                }
                cts.Cancel();
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
            if (r == null) throw new NetException(err.Message + " " + detail);
            throw new Exception(err.Message + " " + detail);
        }

        Task<string> Call(string method, string path, object body)
        {
            if (!HasToken) throw new AuthException();
            return Http(method, Base + path, "Bearer " + token, "application/json", body == null ? null : Util.Json.Serialize(body));
        }

        public HashSet<string> Hidden = new HashSet<string>();   // قوائم مخفية: ما بتنطلب من الشبكة أصلاً
        public List<ProjInfo> LastProjects;                       // قوائم الحساب (للإعدادات وشارة القائمة)

        public async Task<List<TodoTask>> FetchAll()
        {
            var ids = new List<string>();
            if (!Hidden.Contains("inbox")) ids.Add("inbox");
            var projs = new List<ProjInfo>();
            foreach (var o in Util.Arr(Util.Json.DeserializeObject(await Call("GET", "/project", null))))
            {
                var d = o as Dictionary<string, object>;
                if (d == null || Util.S(d, "closed") == "True") continue;
                string id = Util.S(d, "id");
                if (string.IsNullOrEmpty(id)) continue;
                projs.Add(new ProjInfo { Id = id, Name = Util.S(d, "name") ?? "", Color = Util.S(d, "color") });
                if (!Hidden.Contains(id)) ids.Add(id);
            }
            LastProjects = projs;

            var jobs = ids.Select(id => Call("GET", "/project/" + Uri.EscapeDataString(id) + "/data", null)).ToList();
            var all = new List<TodoTask>();
            var seen = new HashSet<string>();
            int failed = 0;
            foreach (var job in jobs)
            {
                string txt;
                try { txt = await job; }
                catch (AuthException) { throw; }
                // فشل شبكة بمشروع واحد: ما بنكمّل بقائمة ناقصة (كانت بتمسح مهام المشاريع الفاشلة من العرض والكاش)
                catch (NetException) { throw; }
                catch (TimeoutException) { throw; }
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
            if (jobs.Count > 0 && failed == jobs.Count) throw new Exception("all project requests failed");
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
        [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RC { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RC r);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetFocus();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            return h != IntPtr.Zero && GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        public const int WM_HOTKEY = 0x0312;
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
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

        // تقليل الذاكرة المحجوزة. مرة كل 10 دقايق بالكتير: التفريغ المتكرر بيجبر الويندوز يرجّع الصفحات مرة ثانية فبيزيد الشغل مش بينقصه
        static DateTime lastTrim = DateTime.MinValue;
        public static void Trim()
        {
            if ((DateTime.Now - lastTrim).TotalMinutes < 10) return;
            lastTrim = DateTime.Now;
            try
            {
                GC.Collect();
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

        public Rect Area = Rect.Empty;   // منطقة عمل شاشة الويدجت (DIP). فاضي = الشاشة الرئيسية

        void Place()
        {
            var wa = Area.IsEmpty ? SystemParameters.WorkArea : Area;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 4;
        }

        // كيبورد (لما النافذة تكون مركّزة: بالاختصار العام أو بضغطة): Enter خلصت، Esc إسكات، 1/2/3 تأجيل
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (current == null || e.Handled) return;
            switch (e.Key)
            {
                case Key.Enter: if (OnDone != null) OnDone(current); e.Handled = true; break;
                case Key.Escape: if (OnDismiss != null) OnDismiss(); e.Handled = true; break;
                case Key.D1: case Key.NumPad1: if (OnSnooze != null) OnSnooze(current, DateTime.Now.AddMinutes(10)); e.Handled = true; break;
                case Key.D2: case Key.NumPad2: if (OnSnooze != null) OnSnooze(current, DateTime.Now.AddHours(1)); e.Handled = true; break;
                case Key.D3: case Key.NumPad3: if (OnSnooze != null) OnSnooze(current, DateTime.Today.AddDays(1).AddHours(9)); e.Handled = true; break;
            }
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

            var close = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, ToolTip = "إسكات (Esc)" };
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
            b0.ToolTip = "Enter"; b1.ToolTip = "1"; b2.ToolTip = "2"; b3.ToolTip = "3";
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
        int tickCount, failCount;
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
        DateTime lastUpdateCheck = DateTime.MinValue;
        bool updating;
        Grid headerGrid, gripLayer;
        ScrollViewer panelScroll;
        Border gripL, gripR, gripB;
        Forms.ToolStripMenuItem lockItem;
        bool dragging, gHoriz, gPhysLeft;
        Native.PT gStart;
        double gW, gL, gT, gH;
        bool placedOnce;
        List<ProjInfo> projects = new List<ProjInfo>();
        bool showChips;
        class RowItem { public TodoTask T; public DueInfo I; }
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

            LoadProjects();
            tasks = LoadCache();
            ApplyHidden();
            if (!api.HasToken) needAuth = true;
            ApplyCollapse();
            Render();

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;

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
                Margin = new Thickness(14)
                // الظل بيتحط من ApplyStyle بس بنمط الزجاج: بدونه بالنمط العادي ما بيتحمّل D3D بالإقلاع
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
            headerGrid = header;
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
                if (st.Locked) return;
                dragging = true;
                try { DragMove(); } catch { }
                dragging = false;
                SavePos();
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
            addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addPick = Pill("", CycleAddProject);
            addPick.Margin = new Thickness(0, 0, 6, 0);
            addPick.Visibility = Visibility.Collapsed;
            addPick.ToolTip = "القائمة للمهمة الجديدة (اضغط للتبديل)";
            addGrid.Children.Add(addPick);
            newBox = new TextBox { Style = (Style)Resources["Input"], ToolTip = "للموعد: @بكرا   @+3   @2026-10-08\nللتنبيه: @17:30   @10د   @ساعة\nلقائمة: #اسم_القائمة (أو زر القائمة جنبه)" };
            newHint = T("＋  إضافة مهمة  ·  @بكرا  ·  #قائمة", 12, "#99FFFFFF");
            newHint.TextTrimming = TextTrimming.CharacterEllipsis;
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
            Grid.SetColumn(newBox, 1);
            Grid.SetColumn(newHint, 1);
            addGrid.Children.Add(newBox);
            addGrid.Children.Add(newHint);
            hairline = new Border { Height = 1, Background = Util.B("#1FFFFFFF"), Margin = new Thickness(2, 8, 2, 0) };
            body.Children.Add(hairline);
            body.Children.Add(addGrid);
            // لوحات الإعدادات والتنبيه السريع جوّا ScrollViewer: الإعدادات صارت أطول من الشاشة (720 DIP على شاشتك)
            panelHost = new StackPanel();
            panelScroll = new ScrollViewer
            {
                Visibility = Visibility.Collapsed,
                MaxHeight = cfg.MaxListHeight + 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panelHost
            };
            body.Children.Add(panelScroll);
            dock.Children.Add(body);

            var outer = new Grid();
            outer.Children.Add(root);
            BuildGrips();
            outer.Children.Add(gripLayer);
            Content = outer;
            ApplyStyle();
            UpdateGrips();
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
            menu.Items.Add(MenuItem("الإعدادات", () => OpenPanel("settings")));
            menu.Items.Add(MenuItem("تحديث", () => { var _ = Refresh(true); }));
            lockItem = new Forms.ToolStripMenuItem("قفل المكان والحجم") { CheckOnClick = true, Checked = st.Locked };
            lockItem.Click += delegate { SetLocked(lockItem.Checked); };
            menu.Items.Add(lockItem);
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

            var baseColor = Util.B(baseFill).Color;
            var hoverColor = Util.B(hoverFill).Color;
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
                if (linkBtn != null) { linkBtn.IsHitTestVisible = true; linkBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110))); }
            };
            border.MouseLeave += delegate
            {
                bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(baseColor, TimeSpan.FromMilliseconds(180)));
                if (linkBtn != null) { linkBtn.IsHitTestVisible = false; linkBtn.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))); }
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
                string pn = ProjectLabel(t);
                string noteTxt = pn.Length == 0 ? t.Note : (string.IsNullOrEmpty(t.Note) || t.Note == t.Title ? pn : pn + " · " + t.Note);
                bool hasNote = !string.IsNullOrEmpty(noteTxt) && noteTxt != t.Title;
                bool isExp = expanded.Contains(t.Id) && !string.IsNullOrWhiteSpace(t.Content);
                if (due.Length > 0 || (hasNote && !isExp))
                {
                    var line = DueLine(due, noteTxt, hot, LvlColor[info.Level], hero ? 12 : 11, !hero && !isExp);
                    ((FrameworkElement)line).Margin = new Thickness(0, hero ? 6 : 3, 0, 0);
                    sp.Children.Add(line);
                }
                if (hero && hasNote && !isExp)
                {
                    var note = T(noteTxt, 12, "#B3FFFFFF");
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
                var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -4, 0), Opacity = expanded.Contains(t.Id) ? 1 : 0, IsHitTestVisible = expanded.Contains(t.Id) };
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
            sb.Append(cfg.Sort).Append(showChips).Append(projects.Count).Append('|').Append(DateTime.Today.Ticks).Append('|').Append(tasks == null).Append(needAuth).Append(connecting).Append('|').Append(statusText).Append('|');
            if (tasks != null)
                foreach (var t in tasks)
                {
                    var i = GetDue(t);
                    sb.Append(t.Id).Append('~').Append(t.Title).Append('~').Append(t.Note).Append('~').Append(t.Content == null ? 0 : t.Content.GetHashCode()).Append('~').Append(t.Link).Append('~').Append(t.Priority)
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

            int visibleLists = (IsHidden("inbox") ? 0 : 1) + projects.Count(p => !IsHidden(p.Id));
            showChips = visibleLists > 1;
            UpdateAddPicker();
            var items = tasks.Select(t => new RowItem { T = t, I = GetDue(t) });
            IOrderedEnumerable<RowItem> sorted;
            switch (cfg.Sort)
            {
                case "priority":
                    sorted = items.OrderByDescending(x => x.T.Priority).ThenBy(x => (int)x.I.Level).ThenBy(x => x.T.Due ?? DateTime.MaxValue);
                    break;
                case "list":
                    sorted = items.OrderBy(x => ProjectOrder(x.T.ProjectId)).ThenBy(x => (int)x.I.Level).ThenBy(x => x.T.Due ?? DateTime.MaxValue).ThenByDescending(x => x.T.Priority);
                    break;
                default:
                    sorted = items.OrderBy(x => (int)x.I.Level).ThenBy(x => x.T.Due ?? DateTime.MaxValue).ThenByDescending(x => x.T.Priority);
                    break;
            }
            var open = sorted.ToList();

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

        // ----------------------------------------------------------- القوائم (المشاريع)
        void LoadProjects()
        {
            try
            {
                string f = Util.D("projects.json");
                if (!File.Exists(f)) return;
                foreach (var o in Util.Arr(Util.Json.DeserializeObject(File.ReadAllText(f, Encoding.UTF8))))
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null) projects.Add(new ProjInfo { Id = Util.S(d, "id"), Name = Util.S(d, "name") ?? "", Color = Util.S(d, "color") });
                }
            }
            catch (Exception ex) { Util.Log("projects read: " + ex.Message); }
        }

        void SaveProjects()
        {
            try { Util.WriteAtomic(Util.D("projects.json"), Util.Json.Serialize(projects.Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "color", p.Color } }).ToList())); }
            catch (Exception ex) { Util.Log("projects save: " + ex.Message); }
        }

        bool IsHidden(string pid)
        {
            if (string.IsNullOrEmpty(pid)) return false;
            return cfg.HiddenProjects.Contains(pid) || (pid.StartsWith("inbox") && cfg.HiddenProjects.Contains("inbox"));
        }

        // القوائم المخفية بتنشال من العرض فوراً (وما بتنجلب بالتحديث الجاي)
        void ApplyHidden()
        {
            api.Hidden = new HashSet<string>(cfg.HiddenProjects);
            if (tasks != null) tasks = tasks.Where(t => !IsHidden(t.ProjectId)).ToList();
        }

        void ToggleProject(string id)
        {
            if (!cfg.HiddenProjects.Remove(id)) cfg.HiddenProjects.Add(id);
            cfg.Save();
            bool hiddenNow = cfg.HiddenProjects.Contains(id);
            ApplyHidden();
            lastSig = null;
            Render();
            if (hiddenNow) SaveCache(); else { var _ = Refresh(true); }   // لما بترجّع قائمة بنجيب مهامها
        }

        // اسم القائمة بالمهمة، بس لما في أكتر من قائمة ظاهرة
        string ProjectLabel(TodoTask t)
        {
            if (!showChips || string.IsNullOrEmpty(t.ProjectId)) return "";
            foreach (var p in projects) if (p.Id == t.ProjectId) return p.Name;
            return t.ProjectId.StartsWith("inbox") ? "الوارد" : "";
        }

        int ProjectOrder(string pid)
        {
            if (!string.IsNullOrEmpty(pid) && pid.StartsWith("inbox")) return -1;
            for (int i = 0; i < projects.Count; i++) if (projects[i].Id == pid) return i;
            return int.MaxValue;
        }

        void BuildListsChooser()
        {
            var all = new List<ProjInfo> { new ProjInfo { Id = "inbox", Name = "الوارد" } };
            all.AddRange(projects);
            var wrapL = new WrapPanel { Margin = new Thickness(-6, -2, 0, 2) };
            foreach (var p in all)
            {
                var proj = p;
                Button b = null;
                Action paint = () =>
                {
                    bool hid = cfg.HiddenProjects.Contains(proj.Id);
                    b.Opacity = hid ? 0.45 : 1;
                    if (hid) b.ClearValue(Control.ForegroundProperty); else b.Foreground = Util.B(Green);
                };
                b = Pill(proj.Name, () => { ToggleProject(proj.Id); paint(); });
                paint();
                wrapL.Children.Add(b);
            }
            AddBlock(wrapL);
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
                try { alarmWin = new AlarmWindow { Area = MonitorWorkAreaDip() }; }
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
            string work = text;
            string pid = EffectiveAddProject();
            var pm = Regex.Match(work, @"(?:^|\s)#(\S+)");
            if (pm.Success)
            {
                string key = pm.Groups[1].Value.Replace('_', ' ');
                string found = FindProject(key);
                if (found == null) { Flash("ما في قائمة باسم " + key); return; }
                pid = found;
                work = work.Remove(pm.Index, pm.Length).Trim();
            }
            string title = work;
            DateTime? due = null;
            bool timed = false;
            var m = Regex.Match(work, @"^(.*?)\s*@(\S+)$");
            if (m.Success)
            {
                var d = ParseWhen(m.Groups[2].Value, out timed);
                if (d != null) { due = d; title = m.Groups[1].Value.Trim(); }
            }
            if (title.Length == 0) title = timed ? "تنبيه" : "";
            if (title.Length == 0) return;
            if (needAuth) { Connect(); return; }
            newBox.Text = "";
            var _ = CreateTask(title, due, timed, text, pid);
            if (timed && due != null) Flash("تنبيه " + Util.When(due.Value));
        }

        // timed = تنبيه: بوقت محدد وبيرن المنبه، وبينحفظ بـ TickTick فبيوصل للجوال كمان
        async Task CreateTask(string title, DateTime? due, bool timed, string restoreText, string projectId = null)
        {
            if (needAuth) { Connect(); return; }
            var body = new Dictionary<string, object> { { "title", title } };
            if (!string.IsNullOrEmpty(projectId) && projectId != "inbox") body["projectId"] = projectId;
            if (due != null)
            {
                body["dueDate"] = Util.ToTT(due.Value);
                body["startDate"] = Util.ToTT(due.Value);
                body["isAllDay"] = !timed;
                body["timeZone"] = cfg.TimeZone;
                if (timed) body["reminders"] = new List<string> { "TRIGGER:PT0S" };
            }
            if (tasks == null) tasks = new List<TodoTask>();
            var temp = new TodoTask { Id = "tmp-" + Guid.NewGuid().ToString("N"), Title = title, Due = due, AllDay = !timed, Content = "", Temp = true, ProjectId = (projectId != null && projectId != "inbox") ? projectId : "" };
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
                if (api.LastProjects != null) { projects = api.LastProjects; SaveProjects(); }
                // المهام يلي لسا بمهلة التراجع بنخليها ظاهرة
                tasks = list;
                needAuth = false;
                lastOk = DateTime.Now;
                statusText = null;
                failCount = 0;
                SaveCache();
            }
            catch (AuthException) { api.ClearToken(); needAuth = true; }
            catch (Exception ex)
            {
                failCount++;
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
                string f = Util.D("cache.json");
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
            try
            {
                string text = Util.Json.Serialize(tasks.Where(t => !t.Temp).Select(t => t.Raw).ToList());
                string f = Util.D("cache.json");
                // نفس المحتوى: ما منعيد كتابة الملف، بس منحدّث وقته (بيُستعمل كـ "آخر تحديث" لما تفتح الأداة)
                if (text == lastCacheText && File.Exists(f)) { File.SetLastWriteTime(f, DateTime.Now); return; }
                lastCacheText = text;
                Util.WriteAtomic(f, text);
            }
            catch (Exception ex) { Util.Log("cache save: " + ex.Message); }
        }

        string lastCacheText;

        // ----------------------------------------------------------- الربط (OAuth)
        class Secret { public string Id, Key; }

        static Secret LoadSecret()
        {
            try
            {
                var d = Util.Json.DeserializeObject(File.ReadAllText(Util.D("ticktick.secret.json"), Encoding.UTF8)) as Dictionary<string, object>;
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
            if (!Util.Isolated) RegisterHotkey(cfg.Hotkey);
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
            if (placedOnce) PlaceWindow();   // الإحداثيات صارت نسبة للأب الجديد
        }

        IntPtr desktopHost;

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeyId)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(HotkeyPressed));
                return IntPtr.Zero;
            }
            // أثناء سحب الرأس: تلصّق بحواف منطقة العمل
            if (msg == Native.WM_WINDOWPOSCHANGING && dragging && desktopHost != IntPtr.Zero)
            {
                try
                {
                    var wp = (Native.WINDOWPOS)Marshal.PtrToStructure(l, typeof(Native.WINDOWPOS));
                    if ((wp.flags & Native.SWP_NOMOVE) == 0) { SnapMove(ref wp); Marshal.StructureToPtr(wp, l, false); }
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        // ----------------------------------------------------------- اختصار عام + إضافة بقائمة
        const int HotkeyId = 0x7A11;
        bool hotkeyOn;
        Button addPick;

        static bool ParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (var part in (s ?? "").Split('+'))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= 2;
                else if (p == "alt") mods |= 1;
                else if (p == "shift") mods |= 4;
                else if (p == "win") mods |= 8;
                else if (p.Length > 0)
                {
                    Key k;
                    if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                    if (!Enum.TryParse(p, true, out k)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                }
            }
            return vk != 0 && mods != 0;
        }

        bool RegisterHotkey(string spec)
        {
            if (hotkeyOn) { Native.UnregisterHotKey(hwnd, HotkeyId); hotkeyOn = false; }
            if (string.IsNullOrWhiteSpace(spec)) return false;
            uint mods, vk;
            if (!ParseHotkey(spec, out mods, out vk)) { Util.Log("hotkey: ما فهمت '" + spec + "'"); return false; }
            hotkeyOn = Native.RegisterHotKey(hwnd, HotkeyId, mods | 0x4000, vk);   // 0x4000 = بدون تكرار مع الضغط المستمر
            if (!hotkeyOn) Util.Log("hotkey: '" + spec + "' مستعمل من برنامج ثاني");
            return hotkeyOn;
        }

        // سطح المكتب ظاهر أصلاً؟ (الواجهة الأمامية هي سطح المكتب نفسه)
        static bool DesktopForeground()
        {
            string c = Native.ClassOf(Native.GetForegroundWindow());
            return c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd";
        }

        // الاختصار: لو في منبه شغّال بيركّزه (Enter خلصت، Esc إسكات، 1/2/3 تأجيل)، وغير هيك بيفتح شريط الإضافة
        void HotkeyPressed()
        {
            if (Util.Isolated) Util.Log("DBG hotkey pressed (alarm=" + (alarmWin != null) + ")");
            if (alarmWin != null) { try { alarmWin.Activate(); } catch { } return; }
            if (newBox.IsKeyboardFocusWithin) { Keyboard.ClearFocus(); if (!Util.Isolated && !DesktopForeground()) Peek(); return; }
            if (st.Collapsed) { st.Collapsed = false; ApplyCollapse(); st.Save(); }
            if (mode != Mode.List) SetMode(Mode.List);
            if (!Util.Isolated && !DesktopForeground()) Peek();   // عرض سطح المكتب عشان الويدجت يبين
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            t.Tick += delegate { t.Stop(); FocusAddBox(); };
            t.Start();
        }

        void FocusAddBox()
        {
            Native.SetForegroundWindow(hwnd);
            Activate();
            newBox.Focus();
            Keyboard.Focus(newBox);
            if (Util.Isolated) Util.Log("DBG FocusAddBox: wpfFocused=" + newBox.IsKeyboardFocused + " fgIsUs=" + (Native.GetForegroundWindow() == hwnd) + " fgClass=" + Native.ClassOf(Native.GetForegroundWindow()));
        }

        string EffectiveAddProject()
        {
            string id = string.IsNullOrEmpty(cfg.AddProject) ? "inbox" : cfg.AddProject;
            if (id != "inbox" && (IsHidden(id) || !projects.Any(p => p.Id == id))) return "inbox";
            return id;
        }

        string ProjectName(string id)
        {
            if (id == "inbox") return "الوارد";
            foreach (var p in projects) if (p.Id == id) return p.Name;
            return "الوارد";
        }

        void UpdateAddPicker()
        {
            if (addPick == null) return;
            addPick.Visibility = showChips ? Visibility.Visible : Visibility.Collapsed;
            addPick.Content = ProjectName(EffectiveAddProject());
        }

        void CycleAddProject()
        {
            var ids = new List<string>();
            if (!IsHidden("inbox")) ids.Add("inbox");
            foreach (var p in projects) if (!IsHidden(p.Id)) ids.Add(p.Id);
            if (ids.Count == 0) return;
            int i = ids.IndexOf(EffectiveAddProject());
            cfg.AddProject = ids[(i + 1) % ids.Count];
            cfg.Save();
            UpdateAddPicker();
        }

        // #اسم بشريط الإضافة: تطابق تام، وإلا بيبدأ بـ، وإلا بيحتوي
        string FindProject(string key)
        {
            key = key.Trim();
            if (key.Equals("inbox", StringComparison.OrdinalIgnoreCase) || key == "الوارد") return "inbox";
            var vis = projects.Where(p => !IsHidden(p.Id)).ToList();
            var hit = vis.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                   ?? vis.FirstOrDefault(p => p.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                   ?? vis.FirstOrDefault(p => p.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
            return hit == null ? null : hit.Id;
        }

        // ----------------------------------------------------------- قفل، تغيير حجم، تلصّق
        const double SnapPx = 12;

        // الحواف المرئية للبطاقة (مش حواف النافذة بهوامش الظل) بتلصق بحواف منطقة العمل
        void SnapMove(ref Native.WINDOWPOS wp)
        {
            var m = ToDevice();
            double mg = rootBorder.Margin.Left * m.M11;
            var o = new Native.PT();
            Native.ClientToScreen(desktopHost, ref o);   // نقطة (0,0) لنافذة الأب بإحداثيات الشاشة
            int sx = wp.x + o.X, sy = wp.y + o.Y;
            var wa = Forms.Screen.FromPoint(new Drawing.Point(sx + wp.cx / 2, sy + 30)).WorkingArea;
            int cl = (int)(sx + mg), cr = (int)(sx + wp.cx - mg), ct = (int)(sy + mg), cb = (int)(sy + wp.cy - mg);
            if (Math.Abs(cl - wa.Left) < SnapPx) sx += wa.Left - cl;
            else if (Math.Abs(cr - wa.Right) < SnapPx) sx += wa.Right - cr;
            if (Math.Abs(ct - wa.Top) < SnapPx) sy += wa.Top - ct;
            else if (Math.Abs(cb - wa.Bottom) < SnapPx) sy += wa.Bottom - cb;
            wp.x = sx - o.X;
            wp.y = sy - o.Y;
        }

        Border MakeGrip(HorizontalAlignment h, VerticalAlignment v, Cursor c, Thickness mg, double w, double hh, bool horizontal)
        {
            var g = new Border { Background = Brushes.Transparent, HorizontalAlignment = h, VerticalAlignment = v, Cursor = c, Margin = mg };
            if (!double.IsNaN(w)) g.Width = w;
            if (!double.IsNaN(hh)) g.Height = hh;
            g.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { GripDown(g, horizontal, e); };
            g.MouseMove += delegate { GripMove(g); };
            g.MouseLeftButtonUp += delegate { GripUp(g); };
            g.LostMouseCapture += delegate { cfg.Save(); };
            return g;
        }

        void BuildGrips()
        {
            gripLayer = new Grid { Margin = rootBorder.Margin };
            gripL = MakeGrip(HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE, new Thickness(-4, 26, 0, 26), 8, double.NaN, true);
            gripR = MakeGrip(HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE, new Thickness(0, 26, -4, 26), 8, double.NaN, true);
            gripB = MakeGrip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS, new Thickness(26, 0, 26, -4), double.NaN, 8, false);
            gripLayer.Children.Add(gripL);
            gripLayer.Children.Add(gripR);
            gripLayer.Children.Add(gripB);
        }

        void UpdateGrips()
        {
            bool free = !st.Locked;
            if (gripL != null)
            {
                gripL.Visibility = gripR.Visibility = free ? Visibility.Visible : Visibility.Collapsed;
                gripB.Visibility = free && !st.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            }
            if (headerGrid != null) headerGrid.Cursor = free ? Cursors.SizeAll : Cursors.Arrow;
        }

        void SetLocked(bool v)
        {
            st.Locked = v;
            st.Save();
            UpdateGrips();
            if (lockItem != null && lockItem.Checked != v) lockItem.Checked = v;
        }

        void GripDown(Border g, bool horizontal, MouseButtonEventArgs e)
        {
            if (st.Locked) return;
            Native.GetCursorPos(out gStart);
            var gp = ScreenPos();
            gW = Width; gL = gp.X; gT = gp.Y; gH = listScroll.ActualHeight;
            gHoriz = horizontal;
            if (horizontal)
            {
                // أي حافة فيزيائياً (الاتجاه RTL بيقلب Left/Right)
                double gx = g.PointToScreen(new Point(g.ActualWidth / 2, 0)).X;
                double cx = PointToScreen(new Point(ActualWidth / 2, 0)).X;
                gPhysLeft = gx < cx;
            }
            g.CaptureMouse();
            e.Handled = true;
        }

        void GripMove(Border g)
        {
            if (!g.IsMouseCaptured) return;
            Native.PT now;
            Native.GetCursorPos(out now);
            var m = ToDevice();
            if (gHoriz)
            {
                double dx = (now.X - gStart.X) / m.M11;
                double nw = Math.Max(260, Math.Min(600, gPhysLeft ? gW - dx : gW + dx));
                if (gPhysLeft) MoveToScreen(gL + (gW - nw), gT);
                Width = nw;
            }
            else
            {
                double dy = (now.Y - gStart.Y) / m.M22;
                listScroll.MaxHeight = Math.Max(150, Math.Min(1400, gH + dy));
                panelScroll.MaxHeight = listScroll.MaxHeight + 80;
            }
        }

        void GripUp(Border g)
        {
            if (!g.IsMouseCaptured) return;
            g.ReleaseMouseCapture();
            cfg.Width = Width;
            cfg.MaxListHeight = listScroll.MaxHeight;
            cfg.Save();
            SavePos();
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

        // النافذة ابنة لنافذة سطح المكتب (Progman/WorkerW) اللي بتغطي كل الشاشات: إحداثياتها نسبة لزاويتها (مش للشاشة).
        // مع شاشة يسار الرئيسية (أو فوقها) بتكون الزاوية سالبة، فبنحوّل صراحةً بدل ما نعتمد على Left/Top بتاعت WPF.
        // مع شاشة وحدة الزاوية (0,0) فالنتيجة مطابقة للقديم
        Native.PT ParentOrigin()
        {
            var o = new Native.PT();
            if (hwnd != IntPtr.Zero && desktopHost != IntPtr.Zero && Native.GetParent(hwnd) == desktopHost) Native.ClientToScreen(desktopHost, ref o);
            return o;
        }

        // موقع النافذة بإحداثيات الشاشة (DIP)
        Point ScreenPos()
        {
            var m = ToDevice();
            Native.RC r;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out r)) return new Point(Left, Top);
            return new Point(r.Left / m.M11, r.Top / m.M22);
        }

        void MoveToScreen(double x, double y)
        {
            if (hwnd == IntPtr.Zero) { Left = x; Top = y; return; }
            var m = ToDevice();
            var o = ParentOrigin();
            Native.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x * m.M11) - o.X, (int)Math.Round(y * m.M22) - o.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        void SavePos()
        {
            var p = ScreenPos();
            st.Left = p.X; st.Top = p.Y;
            st.Save();
        }

        // منطقة عمل الشاشة اللي عليها الويدجت (DIP): المنبه بيطلع هناك مش دايماً على الرئيسية
        Rect MonitorWorkAreaDip()
        {
            var m = ToDevice();
            var wa = (hwnd != IntPtr.Zero ? Forms.Screen.FromHandle(hwnd) : Forms.Screen.PrimaryScreen).WorkingArea;
            return new Rect(wa.Left / m.M11, wa.Top / m.M22, wa.Width / m.M11, wa.Height / m.M22);
        }

        void PlaceWindow()
        {
            placedOnce = true;
            if (st.Left != null && st.Top != null) MoveToScreen(st.Left.Value, st.Top.Value);
            else
            {
                var m = ToDevice();
                var wa = Forms.Screen.PrimaryScreen.WorkingArea;
                MoveToScreen(wa.Right / m.M11 - Width - 4, wa.Top / m.M22 + 4);
            }
            EnsureOnScreen();
        }

        // لو تغيّرت الشاشات/الدقة والنافذة طلعت برا، بنرجعها
        void EnsureOnScreen()
        {
            if (hwnd == IntPtr.Zero) return;
            var m = ToDevice();
            double w = ActualWidth > 0 ? ActualWidth : Width;
            var sp = ScreenPos();
            var head = new Drawing.Rectangle((int)(sp.X * m.M11), (int)(sp.Y * m.M22), (int)(w * m.M11), (int)(60 * m.M22));
            foreach (var s in Forms.Screen.AllScreens)
            {
                var i = Drawing.Rectangle.Intersect(s.WorkingArea, head);
                if (i.Width >= 100 * m.M11 && i.Height >= 30 * m.M22) return;
            }
            var wa = Forms.Screen.PrimaryScreen.WorkingArea;
            MoveToScreen(wa.Right / m.M11 - w - 4, wa.Top / m.M22 + 4);
            SavePos();
        }

        // ----------------------------------------------------------- لوحات التنبيه السريع والإعدادات
        void SetActive(Button b, bool on)
        {
            if (on) b.Foreground = Util.B(Green); else b.ClearValue(Control.ForegroundProperty);
        }

        // --panel=quick|settings|list من سطر الأوامر (للاختبار والاختصارات)
        // أوامر اختبار (وضع --data فقط): بتفحص المنطق بدون تحريك الماوس الحقيقي
        void DebugCmd(string cmd)
        {
            try
            {
                if (cmd == "grips")
                {
                    Func<FrameworkElement, string> r = el =>
                    {
                        var p = el.PointToScreen(new Point(0, 0));
                        return string.Format("x={0:0} y={1:0} w={2:0} h={3:0} vis={4}", p.X, p.Y, el.ActualWidth, el.ActualHeight, el.Visibility);
                    };
                    var wp = PointToScreen(new Point(0, 0));
                    Util.Log("DBG window x=" + wp.X.ToString("0") + " y=" + wp.Y.ToString("0") + " w=" + ActualWidth.ToString("0") + " Width=" + Width.ToString("0") + " Locked=" + st.Locked + " maxList=" + listScroll.MaxHeight.ToString("0"));
                    Util.Log("DBG gripL " + r(gripL) + " | gripR " + r(gripR) + " | gripB " + r(gripB));
                }
                else if (cmd.StartsWith("hotkey="))
                {
                    // يسجّل اختصاراً تجريبياً ويرجّع نتيجة التحليل والتسجيل
                    uint mo, vk;
                    bool parsed = ParseHotkey(cmd.Substring(7), out mo, out vk);
                    Util.Log("DBG hotkey parse=" + parsed + " mods=" + mo + " vk=0x" + vk.ToString("x") + " registered=" + (parsed && RegisterHotkey(cmd.Substring(7))));
                }
                else if (cmd == "focus")
                {
                    FocusAddBox();
                    Util.Log("DBG focus: wpfFocused=" + newBox.IsKeyboardFocused + " win32Focus=" + (Native.GetFocus() == hwnd) + " fgIsUs=" + (Native.GetForegroundWindow() == hwnd) + " fgClass=" + Native.ClassOf(Native.GetForegroundWindow()));
                }
                else if (cmd.StartsWith("add="))
                {
                    newBox.Text = cmd.Substring(4);   // بيجرّب تحليل #قائمة و@ بدون إرسال لـ TickTick (needAuth)
                    var pm = Regex.Match(newBox.Text, @"(?:^|\s)#(\S+)");
                    string found = pm.Success ? FindProject(pm.Groups[1].Value.Replace('_', ' ')) : EffectiveAddProject();
                    Util.Log("DBG add: text='" + newBox.Text + "' -> project=" + (found ?? "NONE") + " picker='" + addPick.Content + "' pickerVisible=" + addPick.Visibility);
                    newBox.Text = "";
                }
                else if (cmd == "pos")
                {
                    var sp = ScreenPos(); var o = ParentOrigin(); var m = ToDevice(); var ps = PointToScreen(new Point(0, 0)); var ar = MonitorWorkAreaDip();
                    Util.Log(string.Format("DBG pos screenDip=({0:0.#},{1:0.#}) parentOriginPx=({2},{3}) dpi={4:0.##} pointToScreenPx=({5:0},{6:0}) workAreaDip={7}", sp.X, sp.Y, o.X, o.Y, m.M11, ps.X, ps.Y, ar));
                }
                else if (cmd.StartsWith("move="))
                {
                    var p = cmd.Substring(5).Split(',');
                    MoveToScreen(double.Parse(p[0], Util.Inv), double.Parse(p[1], Util.Inv));
                    SavePos();
                    var sp = ScreenPos();
                    Util.Log(string.Format("DBG move requested=({0},{1}) now=({2:0.#},{3:0.#}) saved=({4:0.#},{5:0.#})", p[0], p[1], sp.X, sp.Y, st.Left, st.Top));
                }
                else if (cmd == "text") { Util.Log("DBG newBox.Text='" + newBox.Text + "' focused=" + newBox.IsKeyboardFocused); newBox.Text = ""; }
                else if (cmd == "cycle") { CycleAddProject(); Util.Log("DBG cycle -> " + EffectiveAddProject() + " '" + addPick.Content + "'"); }
                else if (cmd.StartsWith("sort="))
                {
                    cfg.Sort = cmd.Substring(5); cfg.Save(); lastSig = null; Render();
                    Util.Log("DBG sort=" + cfg.Sort);
                }
                else if (cmd.StartsWith("toggle="))
                {
                    ToggleProject(cmd.Substring(7));
                    Util.Log("DBG hidden=[" + string.Join(",", cfg.HiddenProjects) + "] tasks=" + (tasks == null ? -1 : tasks.Count) + " chips=" + showChips);
                }
                else if (cmd == "order")
                {
                    Util.Log("DBG order: " + string.Join(" | ", listPanel.Children.OfType<Border>().Select(b => ((TextBlock)((StackPanel)((Grid)b.Child).Children.OfType<StackPanel>().First()).Children[0]).Text)));
                }
                else if (cmd.StartsWith("lock="))
                {
                    SetLocked(cmd.Substring(5) == "1");
                    Util.Log("DBG locked=" + st.Locked);
                }
                else if (cmd.StartsWith("size="))
                {
                    var p = cmd.Substring(5).Split(',');   // عرض,ارتفاع القائمة (يحاكي نتيجة السحب)
                    Width = Math.Max(260, Math.Min(600, double.Parse(p[0], Util.Inv)));
                    listScroll.MaxHeight = Math.Max(150, Math.Min(1400, double.Parse(p[1], Util.Inv)));
                    panelScroll.MaxHeight = listScroll.MaxHeight + 80;
                    cfg.Width = Width; cfg.MaxListHeight = listScroll.MaxHeight; cfg.Save();
                    Util.Log("DBG size w=" + Width + " maxList=" + listScroll.MaxHeight);
                }
                else if (cmd.StartsWith("snap="))
                {
                    var p = cmd.Substring(5).Split(',');   // x,y (إحداثيات الأب) => نتيجة التلصّق
                    var wp = new Native.WINDOWPOS { x = int.Parse(p[0]), y = int.Parse(p[1]), cx = (int)(ActualWidth * ToDevice().M11), cy = (int)(ActualHeight * ToDevice().M22) };
                    var o = new Native.PT();
                    Native.ClientToScreen(desktopHost, ref o);
                    var wa = Forms.Screen.FromPoint(new Drawing.Point(wp.x + o.X + wp.cx / 2, wp.y + o.Y + 30)).WorkingArea;
                    SnapMove(ref wp);
                    Util.Log("DBG snap in=" + p[0] + "," + p[1] + " out=" + wp.x + "," + wp.y + " parentOrigin=" + o.X + "," + o.Y + " workArea=" + wa);
                }
            }
            catch (Exception ex) { Util.Log("DBG error " + ex.Message); }
        }

        // وضع اختبار فقط (--data): رسم الويدجت نفسه لملف PNG بدون التقاط الشاشة
        void SaveShot(string path)
        {
            if (!Util.Isolated) return;
            try
            {
                var el = (FrameworkElement)Content;
                el.UpdateLayout();
                int w = Math.Max(1, (int)Math.Ceiling(ActualWidth)), h = Math.Max(1, (int)Math.Ceiling(ActualHeight));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                // VisualBrush بدل الرسم المباشر: غير هيك النص (RTL) بيطلع معكوس بالـ PNG
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    if (FlowDirection == FlowDirection.RightToLeft) dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, w, 0));   // الرسم خارج النافذة بيطلع معكوس أفقياً
                    dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
                }
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch (Exception ex) { Util.Log("shot: " + ex.Message); }
        }

        public void OpenPanel(string name)
        {
            if (name.StartsWith("shot|")) { SaveShot(name.Substring(5)); return; }
            if (Util.Isolated && name.StartsWith("dbg|")) { DebugCmd(name.Substring(4)); return; }
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
            panelScroll.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
            panelScroll.ScrollToTop();
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
            track.Tag = new Action<bool>(x => { v = x; paint(); });   // لتحديث الحالة من الخارج (بدون استدعاء change)
            return track;
        }

        // الإعدادات مقسّمة لمجموعات: عنوان صغير + بطاقة وحدة فيها صفوف يفصل بينها خط رفيع
        StackPanel curCard;

        void BeginSection(string title, string tip = null)
        {
            var lbl = T(title, 11, "#8CFFFFFF", true);
            lbl.Margin = new Thickness(6, 10, 6, 4);
            if (tip != null) lbl.ToolTip = tip;
            panelHost.Children.Add(lbl);
            curCard = new StackPanel();
            panelHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = Util.B("#17FFFFFF"), Child = curCard });
        }

        void AddBlock(FrameworkElement el)
        {
            if (curCard.Children.Count > 0) curCard.Children.Add(new Border { Height = 1, Background = Util.B("#14FFFFFF"), Margin = new Thickness(11, 0, 11, 0) });
            curCard.Children.Add(new Border { Padding = new Thickness(11, 8, 11, 8), Child = el });
        }

        void AddRow(string label, FrameworkElement ctl, string tip = null)
        {
            var g = new Grid { MinHeight = 24 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = T(label, 12.5, "#E6FFFFFF");
            l.VerticalAlignment = VerticalAlignment.Center;
            l.TextWrapping = TextWrapping.NoWrap;
            if (tip != null) g.ToolTip = tip;
            ctl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ctl, 1);
            g.Children.Add(l);
            g.Children.Add(ctl);
            AddBlock(g);
        }

        Button RowPill(string text, Action act)
        {
            var b = Pill(text, act);
            b.Margin = new Thickness(0);
            return b;
        }

        // زر بتأكيد: الضغطة الأولى بتغيّر النص، والثانية (خلال 3 ثواني) بتنفّذ. للأفعال اللي بتفصل أو بتمسح
        Button ConfirmPill(string text, string confirmText, Action act)
        {
            Button b = null;
            DispatcherTimer reset = null;
            b = Pill(text, () =>
            {
                if (reset != null && reset.IsEnabled) { reset.Stop(); act(); return; }
                b.Content = confirmText;
                b.Foreground = Util.B("#FF6B60");
                if (reset == null)
                {
                    reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    reset.Tick += delegate { reset.Stop(); b.Content = text; b.ClearValue(Control.ForegroundProperty); };
                }
                reset.Stop();
                reset.Start();
            });
            return b;
        }

        void RebuildSettings()
        {
            if (mode != Mode.Settings) return;
            panelHost.Children.Clear();
            BuildSettingsPanel();
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

        void SetAutostart(bool on, FrameworkElement toggle)
        {
            if (Util.Isolated)
            {
                Flash("وضع اختبار: ما بيغيّر التشغيل مع الجهاز");
                ((Action<bool>)toggle.Tag)(!on);
                return;
            }
            Task.Run(() =>
            {
                bool ok = false;
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Util.P("install.ps1") + "\"" + (on ? "" : " -Remove")) { CreateNoWindow = true, UseShellExecute = false };
                    using (var pr = Process.Start(psi)) pr.WaitForExit(20000);
                    ok = TaskExists() == on;
                }
                catch (Exception ex) { Util.Log("autostart: " + ex.Message); }
                if (!ok)   // ما نجح: رجّع المفتاح للحالة الفعلية وبلّغ
                    Dispatcher.BeginInvoke(new Action(() => { ((Action<bool>)toggle.Tag)(!on); Flash("تعذّر تغيير التشغيل مع الجهاز"); }));
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
            // عنوان + زر إغلاق واضح (قبل ما كان الرجوع بس بضغطة الترس مرة ثانية)
            var headRow = new Grid { Margin = new Thickness(3, 0, 0, 0) };
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var head = T("الإعدادات", 14, "#FFFFFF", true);
            head.VerticalAlignment = VerticalAlignment.Center;
            headRow.Children.Add(head);
            var closeBtn = IconButton("", "إغلاق", () => SetMode(Mode.Settings));
            Grid.SetColumn(closeBtn, 1);
            headRow.Children.Add(closeBtn);
            panelHost.Children.Add(headRow);

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

            // التنبيهات: تفاصيل الصوت بتظهر بس لما الصوت شغّال (أقل ازدحام)
            BeginSection("التنبيهات");
            AddRow("الصوت", Toggle(cfg.SoundOn, v =>
            {
                cfg.SoundOn = v; cfg.Save();
                if (v) PreviewSound(); else AlarmSound.Stop();
                Dispatcher.BeginInvoke(new Action(RebuildSettings));
            }));
            if (cfg.SoundOn)
            {
                AddRow("النغمة", Seg(new[] { "جرس", "رقمي", "ناعم" }, Math.Max(0, Array.IndexOf(tones, cfg.Tone)), i => { cfg.Tone = tones[i]; cfg.Save(); PreviewSound(); }));
                AddRow("مستوى الصوت", Seg(new[] { "هادي", "متوسط", "عالي" }, nearest(vols, cfg.Volume), i => { cfg.Volume = vols[i]; cfg.Save(); PreviewSound(); }));
                AddRow("مدة الرنين", Seg(new[] { "30 ث", "90 ث", "مفتوح" }, Math.Max(0, Array.IndexOf(rings, cfg.RingSeconds)), i => { cfg.RingSeconds = rings[i]; cfg.Save(); }));
            }
            AddRow("جرّب المنبه", RowPill("تشغيل", () => { SetMode(Mode.List); TestAlarm(); }));

            BeginSection("المظهر");
            AddRow("الشكل", Seg(new[] { "عادي", "زجاج" }, cfg.Style == "glass" ? 1 : 0, i => { cfg.Style = i == 1 ? "glass" : "mica"; cfg.Save(); ApplyStyle(); }), "الزجاج أثقل بقليل");
            AddRow("الشفافية", Seg(new[] { "خفيف", "متوسط", "غامق" }, nearest(glass, cfg.Opacity), i =>
            {
                cfg.Opacity = glass[i];
                cfg.Save();
                tint.Color = Color.FromArgb((byte)Math.Round(cfg.Opacity * 255), 0x0A, 0x0C, 0x14);
                if (cfg.Style != "glass") SetMica();
            }));
            string[] sorts = { "due", "priority", "list" };
            AddRow("الترتيب", Seg(new[] { "الموعد", "الأهمية", "القائمة" }, Math.Max(0, Array.IndexOf(sorts, cfg.Sort)), i => { cfg.Sort = sorts[i]; cfg.Save(); lastSig = null; Render(); }));

            BeginSection("القوائم", "اضغط على قائمة لإخفائها. المخفية ما بتنجلب ولا بتنبّه");
            BuildListsChooser();

            BeginSection("السلوك");
            // فحص المهمة المجدولة بالخلفية: قبل كانت بتجمّد الواجهة حتى 4 ثواني عند فتح الإعدادات
            FrameworkElement autoTg = null;
            autoTg = Toggle(false, v => SetAutostart(v, autoTg));
            AddRow("التشغيل مع الجهاز", autoTg);
            Task.Run(() => TaskExists()).ContinueWith(tk =>
            {
                if (tk.Status == TaskStatus.RanToCompletion && tk.Result)
                    Dispatcher.BeginInvoke(new Action(() => ((Action<bool>)autoTg.Tag)(true)));
            });
            AddRow("تحديث تلقائي", Toggle(cfg.AutoUpdate, v => { cfg.AutoUpdate = v; cfg.Save(); }), "بيحمّل الإصدار الجديد وبيعيد التشغيل لحاله");
            AddRow("قفل المكان والحجم", Toggle(st.Locked, v => SetLocked(v)), "بيمنع السحب وتغيير الحجم بالغلط");
            AddRow("مكان الويدجت", RowPill("رجّعه للافتراضي", () => { st.Left = null; st.Top = null; PlaceWindow(); st.Save(); }));

            BeginSection("الحساب والتطبيق");
            var acts = new WrapPanel { Margin = new Thickness(-6, -4, 0, 0) };
            acts.Children.Add(Pill("تحديث المهام", () => { SetMode(Mode.List); var _ = Refresh(true); }));
            acts.Children.Add(Pill("فتح TickTick", () => Util.Open("https://ticktick.com/webapp")));
            acts.Children.Add(Pill("فحص التحديث", () => { SetMode(Mode.List); CheckUpdate(true); }));
            acts.Children.Add(ConfirmPill("إعادة الربط", "اضغط للتأكيد", () => { SetMode(Mode.List); api.ClearToken(); needAuth = true; Render(); Connect(); }));
            AddBlock(acts);
            var foot = new Grid { Margin = new Thickness(3, 12, 0, 2) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ver = T("مهامي  ·  الإصدار " + Updater.Current, 11, "#66FFFFFF");
            ver.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(ver);
            var exitPill = Pill("خروج", ExitApp);
            exitPill.Foreground = Util.B("#FF6B60");
            exitPill.Margin = new Thickness(0);
            Grid.SetColumn(exitPill, 1);
            foot.Children.Add(exitPill);
            panelHost.Children.Add(foot);
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
            if (gripLayer != null) gripLayer.Margin = rootBorder.Margin;
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
            var gsp = ScreenPos();
            if (glassBrush != null && Glass.Place(glassBrush, new Rect((gsp.X + mg) * m.M11, (gsp.Y + mg) * m.M22, w * m.M11, h * m.M22))) want = glassBrush;
            if (!ReferenceEquals(rootBorder.Background, want)) rootBorder.Background = want;
        }

        void ApplyCollapse()
        {
            body.Visibility = st.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            summary.Visibility = st.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            (st.Collapsed ? (UIElement)summary : body).BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            UpdateGrips();
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
            // بعد فشل: إعادة محاولة سريعة بتباعد تصاعدي (20 ث، 40، 80...) لحد الفترة العادية
            double wait = cfg.RefreshMinutes * 60.0;
            if (failCount > 0) wait = Math.Min(wait, 20.0 * (1 << Math.Min(failCount - 1, 10)));
            if ((now - lastRefreshTry).TotalSeconds >= wait && !connecting) { var _ = Refresh(); }
            if (cfg.AutoUpdate && !Util.Isolated && !updating && tickCount >= 3 && (now - lastUpdateCheck).TotalHours >= 6) CheckUpdate(false);
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
            failCount = 0;
            lastRefreshTry = DateTime.Now.AddMinutes(-cfg.RefreshMinutes).AddSeconds(15);
        }

        // رجع النت: تحديث بأول تك (خلال 20 ث) بدل ما ننتظر الدورة العادية
        void OnNetworkAvailability(object s, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable) return;
            Dispatcher.BeginInvoke(new Action(() => { failCount = 0; lastRefreshTry = DateTime.MinValue; }));
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

        // ----------------------------------------------------------- التحديث الذاتي
        async void CheckUpdate(bool manual)
        {
            if (updating) return;
            if (Util.Isolated) { if (manual) Flash("وضع اختبار: التحديث معطّل"); return; }
            updating = true;
            lastUpdateCheck = DateTime.Now;
            try
            {
                var u = await Task.Run(() => Updater.FindNewer());
                if (u == null) { if (manual) Flash("أنت على آخر نسخة"); return; }
                if (manual) Flash("جاري تنزيل " + u.Tag + "...");
                string tmp = await Task.Run(() => Updater.Stage(u));
                if (alarmWin != null)   // ما منقاطع منبه شغال
                {
                    lastUpdateCheck = DateTime.Now.AddHours(-5.5);
                    if (manual) Flash("في منبه شغال، جرّب بعد شوي");
                    return;
                }
                Updater.Swap(tmp);
                Util.Log("updated to " + u.Tag);
                RestartNow();
            }
            catch (Exception ex)
            {
                Util.Log("update: " + ex.Message);
                lastUpdateCheck = DateTime.Now.AddHours(-5.5);   // إعادة محاولة بعد نص ساعة
                if (manual) Flash("تعذّر التحديث");
            }
            finally { updating = false; }
        }

        void RestartNow()
        {
            try { Process.Start(new ProcessStartInfo(Updater.ExePath, Util.RestartArgs) { UseShellExecute = false }); }
            catch (Exception ex) { Util.Log("restart: " + ex.Message); return; }
            exiting = true;
            st.Save();
            Cleanup();
            Application.Current.Shutdown();
        }

        // ----------------------------------------------------------- الخروج
        void Cleanup()
        {
            if (hotkeyOn) { Native.UnregisterHotKey(hwnd, HotkeyId); hotkeyOn = false; }
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
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
            try { Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, Util.RestartArgs) { UseShellExecute = false }); }
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
            string dataArg = args.FirstOrDefault(a => a.StartsWith("--data="));
            string shotArg = args.FirstOrDefault(a => a.StartsWith("--shot="));
            string dbgArg = args.FirstOrDefault(a => a.StartsWith("--dbg="));
            if (dataArg != null)
            {
                try
                {
                    Util.DataDir = Path.GetFullPath(dataArg.Substring(7).Trim('"'));
                    Directory.CreateDirectory(Util.DataDir);
                    Util.Isolated = true;
                }
                catch { return 2; }
            }
            string sfx = Util.InstanceSuffix();
            bool testAlarm = args.Contains("--test-alarm");
            string panelArg = args.FirstOrDefault(a => a.StartsWith("--panel="));
            string alarmArg = args.FirstOrDefault(a => a.StartsWith("--alarm="));
            string ringArg = args.FirstOrDefault(a => a.StartsWith("--ring="));
            string dueArg = args.FirstOrDefault(a => a.StartsWith("--due="));
            string renameArg = args.FirstOrDefault(a => a.StartsWith("--rename="));

            bool created;
            var mutex = new Mutex(true, @"Local\AnasTickWidget" + sfx, out created);
            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AnasTickWidget" + sfx + ".Show");
            var testEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AnasTickWidget" + sfx + ".Test");
            var panelEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AnasTickWidget" + sfx + ".Panel");
            if (!created && restart)
            {
                try { created = mutex.WaitOne(15000); }
                catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                if (dueArg != null) { try { File.WriteAllText(Util.D("panel.req"), "due|" + dueArg.Substring(6), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (ringArg != null) { try { File.WriteAllText(Util.D("panel.req"), "ring|" + ringArg.Substring(7), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (renameArg != null) { try { File.WriteAllText(Util.D("panel.req"), "rename|" + renameArg.Substring(9), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (alarmArg != null) { try { File.WriteAllText(Util.D("panel.req"), "alarm|" + alarmArg.Substring(8), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (dbgArg != null && Util.Isolated) { try { File.WriteAllText(Util.D("panel.req"), "dbg|" + dbgArg.Substring(6).Trim('"'), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (shotArg != null && Util.Isolated) { try { File.WriteAllText(Util.D("panel.req"), "shot|" + shotArg.Substring(7).Trim('"'), new UTF8Encoding(false)); } catch { } panelEvt.Set(); }
                else if (panelArg != null) { try { File.WriteAllText(Util.D("panel.req"), panelArg.Substring(8)); } catch { } panelEvt.Set(); }
                else if (testAlarm) testEvt.Set();       // تجربة المنبه على النسخة الشغالة
                else if (!auto) showEvt.Set();      // تشغيل يدوي والأداة شغالة: بس نظهرها
                return 0;
            }

            var state = WidgetState.Load();
            state.UserExited = false;
            if (!Util.Isolated) Updater.Cleanup();
            state.Save();

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 16;   // الافتراضي 2: طلبات القوائم كانت تنتظر بالدور

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
                        try { req = File.ReadAllText(Util.D("panel.req"), Encoding.UTF8).Trim(); File.Delete(Util.D("panel.req")); } catch { }
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
