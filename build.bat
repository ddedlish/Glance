@echo off
rem Requires .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
rem Produces self-contained single-file installers (no .NET needed on target PCs).
if not exist dist mkdir dist
for %%R in (win-x64 win-x86) do (
  dotnet publish src\Glance\Glance.csproj -c Release -r %%R -o out\%%R || exit /b 1
  copy /y out\%%R\Glance.exe dist\Glance-%%R.exe >nul || exit /b 1
)
echo.
echo Done: dist\Glance-win-x64.exe, dist\Glance-win-x86.exe
