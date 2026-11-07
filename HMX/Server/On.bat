
@echo off

set PORT=800

for /f "tokens=5" %%p in ('netstat -ano ^| findstr ":%PORT% " ^| findstr LISTENING') do taskkill /pid %%p /f >nul 2>&1

if not exist ".\log" mkdir ".\log"

start "Server" /min cmd /c "dotnet run --file Source\Server.cs %PORT% 1>.\log\out.log 2>.\log\err.log"

timeout /t 3 >nul
