@echo off
rem Builds TickWidget.exe with the C# compiler that ships with Windows (no install needed)
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /win32manifest:"%~dp0app.manifest" /utf8output /codepage:65001 /out:"%~dp0TickWidget.exe" ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.Security.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0src\TickWidget.cs"
exit /b %ERRORLEVEL%
