@echo off
rem Build FreeMon on Windows (needs only the .NET 10 SDK, no Visual Studio).
rem On Windows the icon and version info are embedded into freemon.exe (not possible when cross-building on Linux).
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo dotnet not found - install .NET 10 SDK & exit /b 1)
rem no telemetry from the build tools (.NET SDK CLI, Avalonia build services)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set AVALONIA_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet publish FreeMon.csproj -r win-x64 -c Release --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -p:DebugSymbols=false -o dist\win-x64
if errorlevel 1 exit /b 1
dir dist\win-x64
