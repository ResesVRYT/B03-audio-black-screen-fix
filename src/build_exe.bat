@echo off
rem Builds "BO3 Audio Fix Installer.exe" with payload\winmm.dll embedded.
rem Uses the C# compiler bundled with .NET Framework - no Visual Studio needed.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set ROOT=%~dp0..

if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler at:
  echo   %CSC%
  exit /b 1
)
if not exist "%ROOT%\payload\winmm.dll" (
  echo Missing payload\winmm.dll - build the DLL first with build.bat
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"%ROOT%\BO3 Audio Fix Installer.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /resource:"%ROOT%\payload\winmm.dll",winmm.dll "%~dp0Bo3AudioFixInstaller.cs"

echo EXITCODE=%errorlevel%
endlocal
