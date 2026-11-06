@echo off
setlocal
set "EXE=%~dp0dist\OCKiller.exe"

if not exist "%EXE%" (
  echo Cannot find "%EXE%".
  echo Build it first:
  echo   dotnet publish .\OCKiller.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o .\dist
  pause
  exit /b 1
)

start "" "%EXE%"
exit /b 0
