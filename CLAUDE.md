# مهامي (TickTick Widget) — دليل للجلسات الجديدة

ويدجت سطح مكتب لـ Windows بيعرض مهام TickTick. ملف واحد: `src/TickWidget.cs` (WPF، C# 5، بدون NuGet).
المستودع: https://github.com/anasashqar/ticktick-widget (**عام**، لأن التحديث الذاتي بيفحص بدون توكن).
المستخدم بيحب الردود بالعربي، مختصرة، وبسيطة. لا push ولا حذف ولا شي نهائي بدون موافقته.

## قواعد صارمة
- **ما تلمس بياناته الحقيقية**: `%APPDATA%\TickWidget\*` (`token.dat`, `secret.dat`, `state.json`, `cache.json`, `settings.json`)، وكمان النسخ القديمة بمجلد المشروع (`token.dat`, `ticktick.secret.json`...) لحد ما المستخدم يوافق على مسحها. ما تقرأ التوكن ولا السر.
- **النسخة الحقيقية مثبّتة** (منذ 2026-11-14، v1.0.4 عبر المثبّت) بـ `%LOCALAPPDATA%\Programs\TickWidget\TickWidget.exe`، ومهمة التشغيل التلقائي `AnasTickWidget` بتشير لها. لا توقف عملية `TickWidget` اللي مسارها هناك ولا تشغّل المثبّت بدون `/SKIPKILL=1` للاختبار. ما تشغّل `build.cmd` من مجلد المشروع إلا بعد تأكد (بيكتب `TickWidget.exe` جنبه؛ مش خطر على المثبّتة). ابنِ للاختبار لمجلد temp.
- **لا لقطة شاشة كاملة** أبداً (التقطت مرة محادثة خاصة). الصور من الويدجت نفسه عبر `--shot`.
- C# 5 فقط (compiler قديم): بدون `?.` و`$"..."` و`nameof` و expression-bodied members.

## البناء والاختبار (بدون لمس الحقيقي)
```powershell
$fw="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"; $wpf="$fw\WPF"; $t="$env:TEMP\tw-test"
New-Item -ItemType Directory -Force "$t\bin","$t\data" | Out-Null; Copy-Item sounds "$t\bin\sounds" -Recurse -Force
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /win32manifest:app.manifest /utf8output /codepage:65001 /out:"$t\bin\TickWidget.exe" `
  /r:"$wpf\PresentationFramework.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\WindowsBase.dll" /r:"$fw\System.Xaml.dll" `
  /r:"$fw\System.Web.Extensions.dll" /r:System.Security.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll src\TickWidget.cs
Start-Process "$t\bin\TickWidget.exe" -ArgumentList "--data=`"$t\data`""     # نسخة معزولة (mutex ومجلد بيانات مختلفين)
```
- ضع مهام وهمية بـ `$t\data\cache.json` (و`projects.json`) لتشوف الواجهة. اللوغ: `$t\data\error.log`.
- أوامر على النسخة المعزولة (استدعاءات ثانية بنفس `--data`): `--panel=settings|quick`، `"--shot=<file.png>"`، `"--dbg=<cmd>"`.
  أوامر `--dbg`: `grips`, `lock=0|1`, `size=W,H`, `snap=x,y`, `sort=due|priority|list`, `toggle=<projectId>`, `order`, `add=<نص>`, `cycle`, `hotkey=<combo>`, `focus`, `text`, `pos`, `move=x,y`.
- بـ PowerShell استعمل علامات اقتباس حول أي وسيط فيه مسافات/عربي: `"--dbg=add=شراء خبز"`.
- إيقاف نسخة الاختبار فقط: `Get-Process TickWidget | ? { $_.Path -like "$env:TEMP\tw-test\*" } | Stop-Process`.

## الإصدار والتحديث الذاتي
- push على `main` بيغيّر `src/**` أو `sounds/**` أو `app.manifest` أو `build.cmd` أو `.github/workflows/build.yml` ⇒ `build.yml` بيحقن الإصدار `1.0.<run_number>` بالمصدر (`Updater.Current`)، بيبني، وبيطلّع Release فيه `TickWidget.exe` + `.sha256` + `TickWidget.zip`.
- الويدجت (`Updater` بالكود) بيفحص `releases/latest` بعد دقيقة من التشغيل وكل 6 ساعات، بيتحقق من sha256، بيعيد تسمية الـ exe الشغّال لـ `.old`، بيحط الجديد، وبيعيد التشغيل (`--restart`). زر **فحص النسخة** بالإعدادات للفحص الفوري. معطّل بوضع `--data`.

## خريطة الكود (`src/TickWidget.cs`)
`Util` (مسارات: `P` جنب الـ exe، `D` للبيانات؛ `Log`) · `Settings` (settings.json) · `Updater` · `WidgetState` (state.json) · `TodoTask` · `Api` (TickTick Open API، `FetchAll`، قوائم مخفية) · `Native` (Win32) · `Glass` · `AlarmWindow` · `Widget` (الواجهة: `BuildUi`, `Render`, `Row`, `BuildSettingsPanel`, الاختصار العام، المقابض/القفل/التلصّق، `ScreenPos/MoveToScreen`) · `Program.Main` (وسائط سطر الأوامر).
- النافذة ابنة لـ Progman/WorkerW (`AttachToDesktop`) ⇒ إحداثياتها نسبة للأب؛ استعمل `ScreenPos()/MoveToScreen()` مش `Left/Top`.

## الحالة (2026-11-14)
- **المرحلة 1 خلصت** (v1.0.3): قفل/حجم/تلصّق، قوائم وترتيب، اختصار عام `Ctrl+Shift+U`، إضافة `#قائمة`، تحويل إحداثيات الشاشات، إعدادات مجمّعة وقابلة للتمرير، تخفيفات أداء.
- **المرحلة 2 خلصت (v1.0.4، 2026-11-14):** المثبّت اتبنى بالـ CI واتجرّب تثبيت وإزالة صامتين بمجلد مؤقت (ملفات، اختصار، سجل: كلها بتنزال نظيفة، ومهمة التشغيل التلقائي الحقيقية ما تأثرت). **اتثبّت رسمياً عند المستخدم**: البيانات اتنقلت (نسخ) لـ `%APPDATA%\TickWidget`، والـ exe بـ `%LOCALAPPDATA%\Programs\TickWidget`. ملفات `D:\Projects\ticktick-widget` القديمة (توكن، سر نصي، إلخ، و`TickWidget.exe`) وكمان `D:\Projects\TickWidget.exe.backup-v1.0.3` **لسا موجودة**: بتنمسح بس بموافقته. التفاصيل: بيانات بـ `%APPDATA%\TickWidget` (`Util.DataDir`، أو جنب الـ exe مع `portable.flag`) + نسخ تلقائي من القديم (`Util.MigrateLegacy`)؛ شاشة ربط `Mode.Setup` وسر مشفّر `SecretStore` (`secret.dat`)؛ مثبّت `installer/TickWidget.iss` بيتبنى بالـ workflow (choco install innosetup) ويطلع `TickWidget-Setup.exe` بالـ Release.
  - اختبار الترحيل بدون لمس الحقيقي: انسخ الـ exe لمجلد مؤقت مع ملفات بيانات وهمية، وشغّله مع `$env:TICKWIDGET_APPDATA="<temp>\appdata"` (بيحوّله لوضع معزول). مثبّت: `TickWidget-Setup.exe /VERYSILENT /SKIPKILL=1 /DIR=<temp> /TASKS=""` (بدون `/SKIPKILL=1` بيقتل أي `TickWidget.exe` شغّال!).
  - بعد أول Release فيه الترحيل: نسخة المستخدم بتتحدث لحالها وبتنسخ بياناتها لـ AppData. الملفات القديمة وملف `ticktick.secret.json` النصي بمجلد المشروع بتضل لحد ما المستخدم يوافق على مسحها.
- **المرحلة 3:** تطبيق أندرويد Kotlin (ويدجت خفيف، RemoteViews) + Firebase للمزامنة. الخطة الكاملة: `C:\Users\sa\.claude\plans\curious-knitting-volcano.md`. نمط البناء/التوقيع منسوخ من `anasashqar/daftar` (`.github/workflows/android.yml`).
- **ما اتجرّب فعلياً:** الكتابة بشريط الإضافة بعد الاختصار، السحب الفعلي بالماوس، شاشة ثانية، PerMonitorV2 (ما انعمل: خطر مع كون النافذة ابن Progman). إزالة WinForms (~19 MB) مؤجلة.
- بيئة المستخدم: Windows 10 (لوحة Widgets غير موجودة)، شاشة 1920×1080 بتكبير 150%، شبكة ببعض الأحيان ما بتحل `api.ticktick.com` (DNS).
