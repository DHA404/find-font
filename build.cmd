@echo off
rem This machine cannot write to %TEMP%\MSBuildTemp, %USERPROFILE%\.templateengine
rem or the NuGet v3 cache. Redirect everything into the repo.
rem Keep this file ASCII-only: cmd.exe parses it with the OEM code page.
setlocal
cd /d "%~dp0"

if not exist ".build\tmp" mkdir ".build\tmp"
if not exist ".build\home" mkdir ".build\home"
if not exist ".nuget\packages" mkdir ".nuget\packages"

set TMP=%CD%\.build\tmp
set TEMP=%CD%\.build\tmp
set NUGET_PACKAGES=%CD%\.nuget\packages
set DOTNET_CLI_HOME=%CD%\.build\home
set DOTNET_CLI_UI_LANGUAGE=en
set DOTNET_NOLOGO=1

if "%~1"=="" goto build
if /I "%~1"=="build" goto build
if /I "%~1"=="test" goto test
if /I "%~1"=="run" goto run
if /I "%~1"=="publish" goto publish
echo Usage: build.cmd [build^|test^|run^|publish]  (test takes optional tier: unit^|integration^|ui)
exit /b 1

:build
dotnet build FindFont.slnx -c Debug || exit /b 1
exit /b 0

:test
dotnet run --project tests\FindFont.SmokeTests\FindFont.SmokeTests.csproj -c Debug -- %2 || exit /b 1
exit /b 0

:run
dotnet run --project src\FindFont.App\FindFont.App.csproj -c Debug || exit /b 1
exit /b 0

:publish
rem 1) main app: framework-dependent single file -> publish\FindFont.App.exe
dotnet publish src\FindFont.App\FindFont.App.csproj -c Release -o publish || exit /b 1
rem 2) launcher: .NET Framework 4.8 bootstrapper -> publish\FindFont.exe (double-click entry)
dotnet build src\FindFont.Launcher\FindFont.Launcher.csproj -c Release || exit /b 1
copy /y "src\FindFont.Launcher\bin\Release\net48\FindFont.exe" "publish\FindFont.exe" >nul || exit /b 1
rem 2b) symbol files are useless to end users: they nearly double the archive size
del /q "publish\*.pdb" 2>nul
echo Publish output: %CD%\publish  (entry: FindFont.exe)
rem 3) portable zip for distribution (version comes from version.txt)
if not exist "dist" mkdir "dist"
set /p APPVER=<version.txt
set ZIPNAME=dist\find-font-%APPVER%-win-x64.zip
if exist "%ZIPNAME%" del /q "%ZIPNAME%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'publish\*' -DestinationPath '%ZIPNAME%' -Force" || exit /b 1
echo Portable zip : %CD%\%ZIPNAME%
exit /b 0
