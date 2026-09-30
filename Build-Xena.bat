@echo off
REM ====================================================================
REM  Build Xena into a single clickable Xena.exe (self-contained).
REM  Output: Xena-App\Xena.exe  (no .NET install required to run it)
REM ====================================================================
echo Building Xena.exe ...
dotnet publish "%~dp0Xena.Desktop\Xena.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0Xena-App"
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  pause
  exit /b 1
)
echo.
echo Done.  Run:  "%~dp0Xena-App\Xena.exe"
pause
