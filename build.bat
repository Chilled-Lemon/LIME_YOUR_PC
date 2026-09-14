@echo off
setlocal

title LIME_YOUR_PC v0.6.4 Builder

echo ==============================================
echo        LIME_YOUR_PC v0.6.4 WPF Builder
echo ==============================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found.
    echo Install the .NET SDK first.
    pause
    exit /b 1
)

echo [1/3] Cleaning...
dotnet clean -c Release >nul 2>nul
if exist "bin\Release\net8.0-windows\win-x64\publish" rmdir /s /q "bin\Release\net8.0-windows\win-x64\publish"

echo [2/3] Publishing win-x64 self-contained single-file...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None

if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    pause
    exit /b 1
)

echo.
echo [3/3] BUILD SUCCESS
echo.
echo EXE:
echo bin\Release\net8.0-windows\win-x64\publish\LIME_YOUR_PC.exe
echo.
pause
endlocal
