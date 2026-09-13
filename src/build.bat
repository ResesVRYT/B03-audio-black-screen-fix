@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "C:\Users\Corbi\AppData\Local\Temp\claude\C--Users-Corbi-Downloads\92cb792e-7f08-46e6-a1de-e28bc72893a7\scratchpad"
cl /nologo /O2 /MT /LD /EHsc /DUNICODE /D_UNICODE winmm_proxy.cpp /Fe:winmm.dll
echo EXITCODE=%errorlevel%
