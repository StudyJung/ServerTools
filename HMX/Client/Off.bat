
@echo off

set TITLE=Client_Console

taskkill /fi "WINDOWTITLE eq %TITLE%*" /t /f >nul 2>&1

timeout /t 3 >nul
