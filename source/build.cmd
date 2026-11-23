@echo off
setlocal
cd /d "%~dp0"
set "FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
"%FX%\csc.exe" /nologo /platform:x64 /target:winexe /codepage:65001 /win32icon:Perelet.ico /win32manifest:app.manifest /out:..\Perelet.exe /resource:Settings.xaml,Settings.xaml /resource:Desk.xaml,Desk.xaml /resource:page.html,page.html /resource:ui.js,ui.js /r:"%FX%\WPF\PresentationCore.dll" /r:"%FX%\WPF\PresentationFramework.dll" /r:"%FX%\WPF\WindowsBase.dll" /r:..\QRCoder.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xaml.dll Desk.cs Session.cs LocalHttp.cs Link.cs FolderPicker.cs Preferences.cs Bundle.cs Transfers.cs LinkQr.cs Themes.cs
if errorlevel 1 exit /b 1
copy /y Perelet.exe.config ..\Perelet.exe.config >nul


