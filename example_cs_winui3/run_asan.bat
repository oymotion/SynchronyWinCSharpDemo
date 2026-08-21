@echo off
REM Run the demo with the ASan-built sensor.dll; reports land in
REM asan_winui3.log.<pid> next to the exe. Delete asan_winui3.log.* and the two
REM dll copies to go back to normal (a rebuild restores the regular dll).
copy /y F:\workspace\SensorSDKCXX\build\asan_dll\sensor.dll "%~dp0sensor.dll" >nul
copy /y F:\workspace\SensorSDKCXX\build\asan_dll\clang_rt.asan_dynamic-x86_64.dll "%~dp0clang_rt.asan_dynamic-x86_64.dll" >nul
set ASAN_OPTIONS=log_path=asan_winui3.log
start "" "%~dp0example_cs_winui3.exe"
