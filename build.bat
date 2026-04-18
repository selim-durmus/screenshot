@echo off
setlocal

rem Double-click this to build ScreenshotOCR.exe. The built exe will land
rem right next to this script (the MSBuild CopyPublishedExeToRoot target
rem moves it out of bin\Release\... for you).

rem Stop any running instance so the exe isn't locked by MSBuild's copy.
taskkill /f /im ScreenshotOCR.exe >nul 2>&1

dotnet publish -c Release -r win-x64 --self-contained true

if %errorlevel% equ 0 (
    echo.
    echo Build succeeded. Launching ScreenshotOCR...
    start "" "%~dp0ScreenshotOCR.exe"
    timeout /t 2 >nul
) else (
    echo.
    echo Build FAILED. Press any key to close.
    pause >nul
)

endlocal
