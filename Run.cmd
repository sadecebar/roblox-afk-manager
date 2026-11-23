@echo off
setlocal
rem The workspace's SDK already includes Desktop and ASP.NET Core 10 runtimes.
rem Extracted packages elsewhere use the installed system runtimes.
if exist "%~dp0..\..\.dotnet\dotnet.exe" set "DOTNET_ROOT_X64=%~dp0..\..\.dotnet"
if not exist "%~dp0RobloxAfkManager.exe" (
  echo Run this launcher inside the published application folder.
  exit /b 1
)
start "" "%~dp0RobloxAfkManager.exe"
