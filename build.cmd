@echo off
rem Builds QwenStudio.exe into dist\ together with the config templates.
rem Needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
setlocal
cd /d "%~dp0"
dotnet publish src\QwenStudio\QwenStudio.csproj -c Release -o dist || exit /b 1
copy /y profiles.json dist\ >nul
if not exist dist\server_config.env copy /y server_config.example.env dist\server_config.env >nul
echo.
echo Done: dist\QwenStudio.exe
