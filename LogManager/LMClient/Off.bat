
@echo off

set TITLE=LMClient_Console

taskkill /fi "WINDOWTITLE eq %TITLE%*" /t /f >nul 2>&1

timeout /t 3 >nul
