@echo off
REM ===========================================================================
REM  Cerner -> Epic migration : multi-instance client demo
REM
REM  Starts INSTANCES copies of the application against ONE input root. No
REM  folder name appears anywhere in this script: each instance discovers the
REM  folders under the share at runtime and claims the ones no other instance
REM  holds, so 10 folders or 100 folders need no change here.
REM
REM  Usage:  run_demo.bat            start the instances
REM          run_demo.bat --instance N   (internal - one instance in one window)
REM ===========================================================================

setlocal

set "DEMO=D:\Davita\fileconversion_poc\demo"
set "BIN=%DEMO%\bin"
set "SHARE=%DEMO%\share"
set "OUT=%DEMO%\output\rtf"
set "COORD=%DEMO%\coordination"
set "REPORTS=%DEMO%\reports"

REM Instances to start, and workers inside each one (3 x 4 = 12 on a 12-core host).
set "INSTANCES=3"
set "THREADS=4"

if /I "%~1"=="--instance" goto :run_one

REM ----------------------------------------------------------------- launcher
echo.
echo  Input root : %SHARE%
echo  Output     : %OUT%
echo  Claims     : %COORD%
echo  Starting %INSTANCES% instance(s), %THREADS% worker(s) each.
echo.

if not exist "%BIN%\CernerToEpicMigration.exe" (
    echo ERROR: application not found at %BIN%\CernerToEpicMigration.exe
    exit /b 2
)

for /L %%I in (1,1,%INSTANCES%) do (
    echo  Starting Instance-%%I ...
    start "Migration Instance-%%I" cmd /k ""%~f0" --instance %%I"
)

echo.
echo  All %INSTANCES% instance(s) started - each in its own window.
echo  Watch the windows, or Task Manager for %INSTANCES% x CernerToEpicMigration.exe.
echo.
endlocal
goto :eof

REM ------------------------------------------------------------- one instance
:run_one
set "N=%~2"
title Migration Instance-%N%

REM Each instance keeps its own reports, logs and checkpoint: the application's
REM run lock is per report folder, so a shared one would refuse to start.
set "MigrationConfig__ReportBasePath=%REPORTS%\instance%N%"
set "MigrationConfig__LogBasePath=%REPORTS%\instance%N%\logs"

"%BIN%\CernerToEpicMigration.exe" ^
    --input "%SHARE%" ^
    --output "%OUT%" ^
    --coordination "%COORD%" ^
    --instance-id "Instance-%N%" ^
    --threads %THREADS%

echo.
echo ===========================================================
echo  Instance-%N% finished. Exit code %ERRORLEVEL%.
echo   0 = all converted   1 = completed with failures
echo   2 = bad arguments   3 = fatal
echo ===========================================================
endlocal
goto :eof
