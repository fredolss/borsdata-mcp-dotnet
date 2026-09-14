@echo off
setlocal enabledelayedexpansion
for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%~dp0.env") do (
    if not "%%A"=="" set "%%A=%%B"
)
dotnet run --project "%~dp0src\BorsdataMcp"
endlocal
