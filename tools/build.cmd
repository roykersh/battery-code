@echo off
rem Builds bin\batteryfloat.exe with the C# compiler that ships with Windows (.NET Framework 4).
rem winexe: no console window. The .winmd files give it the Windows Bluetooth LE advert API.
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
if not exist "%~dp0..\bin" mkdir "%~dp0..\bin"
"%NET%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:"%~dp0..\bin\batteryfloat.exe" ^
  /r:"%WM%\Windows.Devices.winmd" /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Storage.winmd" ^
  /r:"%NET%\System.Runtime.dll" /r:"%NET%\System.Runtime.WindowsRuntime.dll" /r:"%NET%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0batteryfloat.cs"
