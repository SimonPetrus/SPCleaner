@echo off
setlocal
rem SPCleaner+ ikonunu yeniden cizer: ..\SPCleaner+.ico (sonra build.bat calistir)
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
cd /d "%~dp0"
"%CSC%" /nologo /out:"%TEMP%\SPIkonOlustur.exe" /r:System.Drawing.dll IkonOlustur.cs
if errorlevel 1 (echo.& echo IKON URETECI DERLENEMEDI & exit /b 1)
"%TEMP%\SPIkonOlustur.exe" "%~dp0..\SPCleaner+.ico"
del "%TEMP%\SPIkonOlustur.exe" >nul 2>&1
endlocal
