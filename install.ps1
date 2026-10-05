# يسجّل مهمة بـ Task Scheduler: تشغيل الويدجت عند دخولك للويندوز (تأخير 10 ثواني)،
# ويرجّعه لحاله لو وقع. بدون صلاحيات مدير.
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $dir 'TickWidget.exe'
$name = 'AnasTickWidget'
$user = "$env:USERDOMAIN\$env:USERNAME"

# الاختصار القديم (نسخة PowerShell) كان بيشغّل أداة ثانية جنبها
$oldLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Anas Reminders.lnk'
if (Test-Path $oldLink) { Remove-Item $oldLink -Force; Write-Host 'تم حذف اختصار التشغيل القديم' }

if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'تم حذف مهمة التشغيل التلقائي'
    return
}

if (-not (Test-Path $exe)) { throw "TickWidget.exe مش موجود. شغّل build.cmd أول." }
Get-ChildItem -LiteralPath $dir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$action   = New-ScheduledTaskAction -Execute $exe -Argument '--auto' -WorkingDirectory $dir
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = 'PT10S'
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Write-Host "تم تسجيل المهمة '$name'"

if (-not (Get-Process TickWidget -ErrorAction SilentlyContinue)) { Start-Process $exe }
