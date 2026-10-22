@echo off
rem Builds the native Windows launcher (WinUI 3) into out\launcher\Bloodborne.exe.
rem Needs the .NET 10 SDK (https://dot.net). launch_gui.bat starts it when present.
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet not found. Install the .NET 10 SDK from https://dot.net and try again.
    exit /b 1
)
set "dest=%~dp0..\out\launcher"
dotnet publish "BloodborneLauncher\BloodborneLauncher.csproj" -c Release -r win-x64 -p:Platform=x64 -o "%dest%"
if errorlevel 1 (
    echo [ERROR] Launcher build failed.
    exit /b 1
)
echo.
echo Launcher built: %dest%\Bloodborne.exe
exit /b 0
