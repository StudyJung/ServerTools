
@echo off

set TITLE=LMClient_Console

taskkill /fi "WINDOWTITLE eq %TITLE%*" /t /f >nul 2>&1

if not exist ".\log" mkdir ".\log"

start "%TITLE%" /min cmd /c "dotnet run --file LMClient.cs 1>.\log\out.log 2>.\log\err.log"

timeout /t 3 >nul
