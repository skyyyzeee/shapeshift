@echo off
rem Bench stub installer: writes a marker file (%1) and an HKLM value; the latter needs administrator rights.
if not exist "%~dp1" mkdir "%~dp1"
echo installed> "%~1"
reg.exe add HKLM\SOFTWARE\ShapeshiftBench /v Elevated /t REG_SZ /d 1 /f
