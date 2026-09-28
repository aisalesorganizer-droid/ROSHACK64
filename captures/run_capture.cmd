@echo off
setlocal
cd /d "%~dp0"
if "%~1"=="" (
  dotnet run -c Release -- 20 2
) else (
  dotnet run -c Release -- %*
)
endlocal
