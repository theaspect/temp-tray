@echo off
rem Builds TempTray.exe with the C# compiler that ships with Windows (.NET Framework 4.8): no SDK, no packages.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /out:"%~dp0TempTray.exe" /win32manifest:"%~dp0app.manifest" /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0TempTray.cs"
