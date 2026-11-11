@echo off
REM Builds bin\FreeUSBEject.exe with the C# compiler included in Windows (.NET Framework 4).
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4 compiler not found.
  pause
  exit /b 1
)
if not exist bin mkdir bin
if exist "assets\FreeUSBEject.ico" (
  "%CSC%" /nologo /target:winexe /out:"bin\FreeUSBEject.exe" "/win32icon:assets\FreeUSBEject.ico" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll "src\FreeUSBEject.cs"
) else (
  "%CSC%" /nologo /target:winexe /out:"bin\FreeUSBEject.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll "src\FreeUSBEject.cs"
)
if errorlevel 1 (echo Build FAILED) else (echo Built bin\FreeUSBEject.exe)
pause
