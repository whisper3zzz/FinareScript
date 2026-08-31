@echo off
setlocal
set "US_CLI=%~dp0UnifiedScript.Cli\UnifiedScript.Cli.exe"
if not exist "%US_CLI%" (
  echo UnifiedScript CLI is missing: %US_CLI% 1>&2
  exit /b 66
)
"%US_CLI%" %*
exit /b %ERRORLEVEL%
