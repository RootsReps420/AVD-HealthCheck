@echo off
rem Builds dist\AVDHealthCheck.exe with the C# compiler that ships with Windows
rem (.NET Framework 4.8). Nothing to install: double-click this file or run it from a prompt.
setlocal
cd /d "%~dp0"

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo Could not find csc.exe from .NET Framework 4.x.
  exit /b 1
)

if not exist dist mkdir dist

"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /debug- ^
  /out:dist\AVDHealthCheck.exe ^
  /win32icon:assets\app.ico ^
  /win32manifest:src\app.manifest ^
  /reference:"%FW%\WPF\PresentationFramework.dll" ^
  /reference:"%FW%\WPF\PresentationCore.dll" ^
  /reference:"%FW%\WPF\WindowsBase.dll" ^
  /reference:"%FW%\System.Xaml.dll" ^
  /reference:"%FW%\System.Net.Http.dll" ^
  /reference:"%FW%\System.Management.dll" ^
  /reference:"%FW%\System.Web.Extensions.dll" ^
  /reference:"%FW%\Microsoft.CSharp.dll" ^
  /reference:"%FW%\System.Core.dll" ^
  /reference:System.dll ^
  /resource:src\Ui\MainWindow.xaml,MainWindow.xaml ^
  /resource:src\Ui\horse-dark.txt,horse-dark.txt ^
  /resource:src\Ui\horse-grey.txt,horse-grey.txt ^
  /resource:assets\app.ico,app.ico ^
  /resource:config\endpoints.json,endpoints.json ^
  /recurse:src\*.cs

if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

echo.
echo Built dist\AVDHealthCheck.exe
