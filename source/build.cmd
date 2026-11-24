@echo off
setlocal
cd /d "%~dp0"
set "FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
"%FX%\csc.exe" /nologo /platform:x64 /target:winexe /codepage:65001 /win32icon:Blink.ico /win32manifest:app.manifest /out:..\Blink.exe /resource:Settings.xaml,Settings.xaml /resource:dagger.png,dagger.png /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /resource:Desk.xaml,Desk.xaml /resource:page.html,page.html /resource:ui.js,ui.js /r:"%FX%\WPF\PresentationCore.dll" /r:"%FX%\WPF\PresentationFramework.dll" /r:"%FX%\WPF\WindowsBase.dll" /r:..\QRCoder.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xaml.dll Desk.cs Session.cs LocalHttp.cs Link.cs FolderPicker.cs Preferences.cs Bundle.cs Transfers.cs LinkQr.cs Themes.cs Extras.cs Boot.cs Updates.cs
if errorlevel 1 exit /b 1
copy /y Blink.exe.config ..\Blink.exe.config >nul



"%FX%\csc.exe" /nologo /target:winexe /platform:x64 /win32icon:Blink.ico /out:..\BlinkLauncher.exe /r:System.Windows.Forms.dll Launcher.cs Boot.cs

