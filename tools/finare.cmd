@echo off
setlocal
set "US_CLI=%~dp0Finare.Cli\Finare.Cli.exe"
if not exist "%US_CLI%" (
  echo Finare CLI is missing: %US_CLI% 1>&2
  exit /b 66
)
"%US_CLI%" %*
exit /b %ERRORLEVEL%
