@echo off
setlocal
cd /d "%~dp0"
set "FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "RUST_RESOURCE=/resource:..\native\target\release\blink_core.dll,blink_core.dll"
if "%BLINK_MANAGED_ONLY%"=="1" (
    set "RUST_RESOURCE="
) else if not exist "..\native\target\release\blink_core.dll" (
    echo Missing Rust core. Build native with cargo build --release --locked first. >&2
    exit /b 1
)
"%FX%\csc.exe" /nologo /optimize+ /platform:x64 %RUST_RESOURCE% /target:winexe /codepage:65001 /win32icon:Blink.ico /win32manifest:app.manifest /out:..\Blink.exe /resource:Settings.xaml,Settings.xaml /resource:dagger.png,dagger.png /r:System.IO.Compression.FileSystem.dll /r:System.ServiceModel.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /resource:Desk.xaml,Desk.xaml /resource:page.html,page.html /resource:ui.js,ui.js /r:"%FX%\WPF\PresentationCore.dll" /r:"%FX%\WPF\PresentationFramework.dll" /r:"%FX%\WPF\WindowsBase.dll" /r:..\QRCoder.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xaml.dll Desk.cs Session.cs LocalHttp.cs Link.cs FolderPicker.cs Preferences.cs Bundle.cs Transfers.cs LinkQr.cs Themes.cs Extras.cs Boot.cs Updates.cs TransferReader.cs
if errorlevel 1 exit /b 1
copy /y Blink.exe.config ..\Blink.exe.config >nul



"%FX%\csc.exe" /nologo /optimize+ /target:winexe /platform:x64 /win32icon:Blink.ico /out:..\BlinkLauncher.exe /r:System.Windows.Forms.dll Launcher.cs Boot.cs

