@echo off
rem Builds the proxy winmm.dll into payload\winmm.dll.
rem Needs Visual Studio with the C++ workload. Intermediates go to %TEMP% so
rem this folder stays clean. Afterwards run build_exe.bat to embed the new DLL.
setlocal
set ROOT=%~dp0..

set VS=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VS%" set VS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VS%" set VS=C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VS%" set VS=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VS%" (
  echo Could not find vcvars64.bat.
  echo Edit this file and point VS at the vcvars64.bat in your Visual Studio install.
  exit /b 1
)

call "%VS%" >nul 2>&1

set OBJDIR=%TEMP%\bo3_dll_build
if not exist "%OBJDIR%" mkdir "%OBJDIR%"
pushd "%OBJDIR%"

cl /nologo /O2 /MT /LD /EHsc /DUNICODE /D_UNICODE "%~dp0winmm_proxy.cpp" /I"%~dp0." /Fe:"%ROOT%\payload\winmm.dll"
set RC=%errorlevel%

popd
echo EXITCODE=%RC%
if "%RC%"=="0" echo Built: %ROOT%\payload\winmm.dll
endlocal
