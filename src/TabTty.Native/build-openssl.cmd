@echo off
setlocal
if "%~3"=="" exit /b 2
set "SOURCE=%~f1"
set "PERL=%~f2"
set "OUTPUT=%~f3"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT exit /b 3
call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
pushd "%SOURCE%"
"%PERL%" Configure VC-WIN64A-HYBRIDCRT shared no-asm no-tests no-apps no-docs no-legacy no-module no-autoload-config no-makedepend no-engine
if errorlevel 1 goto failed
nmake /nologo build_generated
if errorlevel 1 goto failed
nmake /nologo libcrypto-3-x64.dll
if errorlevel 1 goto failed
if not exist "%OUTPUT%" mkdir "%OUTPUT%"
copy /y libcrypto-3-x64.dll "%OUTPUT%\libcrypto-3-x64.dll" >nul
if errorlevel 1 goto failed
copy /y LICENSE.txt "%OUTPUT%\LICENSE" >nul
if errorlevel 1 goto failed
popd
exit /b 0
:failed
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
