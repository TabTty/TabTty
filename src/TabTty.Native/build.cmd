@echo off
setlocal
if "%~1"=="" exit /b 2
set "OUTPUT=%~f1"
if not exist "%OUTPUT%" mkdir "%OUTPUT%"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT exit /b 3
call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
pushd "%OUTPUT%"
cl /nologo /W4 /O2 /MT /GS /guard:cf /DWIN32_LEAN_AND_MEAN /D_CRT_SECURE_NO_WARNINGS /I"%~dp0." /I"%~dp0openbsd" /LD "%~dp0bcrypt_adapter.c" "%~dp0openbsd\bcrypt_pbkdf.c" "%~dp0openbsd\blowfish.c" /link /OUT:TabTty.Native.dll /DYNAMICBASE /NXCOMPAT /guard:cf
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
