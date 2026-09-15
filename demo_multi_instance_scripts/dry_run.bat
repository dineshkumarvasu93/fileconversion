@echo off
REM ===========================================================================
REM  Safe rehearsal / demo step 1 and 3: lists the folders the application
REM  discovers under the input root and the file count in each.
REM
REM  Nothing is converted, moved or written to the share.
REM ===========================================================================

setlocal

set "DEMO=D:\Davita\fileconversion_poc\demo"
set "REPORTS=%DEMO%\reports\dryrun"

set "MigrationConfig__ReportBasePath=%REPORTS%"
set "MigrationConfig__LogBasePath=%REPORTS%\logs"

"%DEMO%\bin\CernerToEpicMigration.exe" --input "%DEMO%\share" --dry-run

echo.
echo These folders were discovered at runtime - none of them is named in any
echo script or configuration file.
echo.
pause
endlocal
