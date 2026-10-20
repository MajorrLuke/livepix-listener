@echo off
setlocal
cd /d "%~dp0"
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REF=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%CSC%" (
  echo Nao achei o compilador do .NET Framework.
  exit /b 1
)
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:app.manifest /out:dist\LivePixListener.exe ^
  /reference:"%REF%\System.Windows.Forms.dll" ^
  /reference:"%REF%\System.Drawing.dll" ^
  /reference:"%REF%\System.Net.Http.dll" ^
  /reference:"%REF%\System.Web.Extensions.dll" ^
  /reference:"%REF%\WPF\PresentationCore.dll" ^
  /reference:"%REF%\WPF\WindowsBase.dll" ^
  /reference:"%REF%\WPF\PresentationFramework.dll" ^
  src\Program.cs
if errorlevel 1 (
  echo.
  echo A compilacao falhou.
  exit /b 1
)
echo.
echo Pronto: dist\LivePixListener.exe