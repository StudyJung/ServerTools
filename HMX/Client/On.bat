
@echo off

set TITLE=Client_Console

taskkill /fi "WINDOWTITLE eq %TITLE%*" /t /f >nul 2>&1

if not exist ".\log" mkdir ".\log"

start "%TITLE%" /min cmd /c "dotnet run --file Source\Client.cs 1>.\log\out.log 2>.\log\err.log"

timeout /t 3 >nul
