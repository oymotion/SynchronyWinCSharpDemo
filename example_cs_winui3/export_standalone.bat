@echo off
REM Export the WinUI3 demo as a standalone project: demo sources + C# bindings
REM + Release sensor.dll (x64 + x86), keeping the repo-relative layout so the
REM csproj works unmodified. Usage: export_standalone.bat [target_dir]
setlocal
set TARGET=%~1
if "%TARGET%"=="" set TARGET=F:\workspace\SynchronyWinCSharpDemo
set ROOT=%~dp0..

robocopy "%~dp0." "%TARGET%\example_cs_winui3" /MIR /XD bin obj .vs /XF *.user /NFL /NDL /NJH
if errorlevel 8 goto :fail
robocopy "%ROOT%\bindings\csharp" "%TARGET%\bindings\csharp" SensorCapi.cs Sensor.cs README.md /NFL /NDL /NJH
if errorlevel 8 goto :fail
robocopy "%ROOT%\lib\windows\x64\Release" "%TARGET%\lib\windows\x64\Release" sensor.dll /NFL /NDL /NJH
if errorlevel 8 goto :fail
robocopy "%ROOT%\lib\windows\Win32\Release" "%TARGET%\lib\windows\Win32\Release" sensor32.dll /NFL /NDL /NJH
if errorlevel 8 goto :fail

REM the export ships Release dlls only; make Release the default there
powershell -NoProfile -Command "(Get-Content '%TARGET%\example_cs_winui3\example_cs_winui3.csproj' -Raw) -replace '>[$][(]Configuration[)]</SensorSdkConfig>', '>Release</SensorSdkConfig>' | Set-Content '%TARGET%\example_cs_winui3\example_cs_winui3.csproj' -NoNewline"
if errorlevel 1 goto :fail

echo Exported to %TARGET%
echo Build x64: dotnet build "%TARGET%\example_cs_winui3\example_cs_winui3.csproj" -p:SensorSdkConfig=Release
echo Build x86: dotnet build "%TARGET%\example_cs_winui3\example_cs_winui3.csproj" -p:SensorSdkConfig=Release -p:SensorSdkArch=x86
exit /b 0
:fail
echo Export failed (robocopy error)
exit /b 1
