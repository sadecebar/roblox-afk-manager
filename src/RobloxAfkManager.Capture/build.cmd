@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo Install Visual Studio C++ desktop build tools and the Windows 10/11 SDK.
  exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH exit /b 1
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
if "%~1"=="" exit /b 1
set "CAPTURE_OUTPUT=%~f1"
if not exist "%CAPTURE_OUTPUT%" mkdir "%CAPTURE_OUTPUT%"
rem A relative source filename keeps __FILE__ in WinRT diagnostics free of home paths.
pushd "%~dp0"
if errorlevel 1 exit /b 1
cl /nologo /std:c++20 /EHsc /O2 /MT /W4 /LD /DWIN32_LEAN_AND_MEAN /DNOMINMAX Capture.cpp /Fo"%CAPTURE_OUTPUT%\Capture.obj" /link /OUT:"%CAPTURE_OUTPUT%\RobloxAfkManager.Capture.dll" /IMPLIB:"%CAPTURE_OUTPUT%\Capture.lib" d3d11.lib dxgi.lib windowsapp.lib dwmapi.lib user32.lib
set "CAPTURE_BUILD_EXIT=%errorlevel%"
popd
exit /b %CAPTURE_BUILD_EXIT%
