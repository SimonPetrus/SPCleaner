@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=SPCleaner+.exe
cd /d "%~dp0"
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /platform:x64 /win32manifest:app.manifest /win32icon:"SPCleaner+.ico" /out:"%OUT%" ^
 /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
 "SPCleaner+.cs"
if errorlevel 1 (echo.& echo DERLEME BASARISIZ & exit /b 1)
echo.
echo Olusturuldu: %~dp0%OUT%
endlocal
