@echo off
setlocal
cd /d "%~dp0"

rem The native launcher (launcher-win\build-launcher.bat builds it) when present; else the
rem Python one. Exactly one starts. Either inherits this environment, so variables set by
rem the caller ("Play Bloodborne.bat") reach the game.
if exist "%~dp0out\launcher\Bloodborne.exe" (
    start "" "%~dp0out\launcher\Bloodborne.exe"
    exit /b 0
)

set "PYTHON_EXE="
for %%P in (
    "C:\Program Files\Python313\python.exe"
    "C:\Program Files\Python312\python.exe"
    "C:\Program Files\Python311\python.exe"
    "C:\Program Files\Python310\python.exe"
    "%LocalAppData%\Programs\Python\Python313\python.exe"
    "%LocalAppData%\Programs\Python\Python312\python.exe"
    "%LocalAppData%\Programs\Python\Python311\python.exe"
    "%LocalAppData%\Programs\Python\Python310\python.exe"
) do (
    if not defined PYTHON_EXE if exist %%P set "PYTHON_EXE=%%~P"
)

if not defined PYTHON_EXE (
    for /f "delims=" %%I in ('where python.exe 2^>nul') do (
        echo "%%~I" | findstr /i /c:"WindowsApps" >nul
        if errorlevel 1 (
            if not defined PYTHON_EXE set "PYTHON_EXE=%%~I"
        )
    )
)

if not defined PYTHON_EXE (
    echo [ERROR] Python not found. Please install Python 3.10+ and add it to PATH,
    echo or build the native launcher with launcher-win\build-launcher.bat.
    pause
    exit /b 1
)

start "" "%PYTHON_EXE%" launcher.py
