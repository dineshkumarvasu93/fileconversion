# ===========================================================================
#  Post-run validation for the multi-instance demo (guide section 10, step 7).
#  Read-only: it inspects the coordination folder, the share and the reports.
# ===========================================================================

$demo    = 'D:\Davita\fileconversion_poc\demo'
$share   = Join-Path $demo 'share'
$out     = Join-Path $demo 'output\rtf'
$coord   = Join-Path $demo 'coordination'
$reports = Join-Path $demo 'reports'

function Head($text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Cyan
    Write-Host ('-' * $text.Length) -ForegroundColor Cyan
}

# --- 1. Which instance processed which folder -------------------------------
Head 'Folder ownership (one .done per folder = processed exactly once)'

$done = @(Get-ChildItem -Path $coord -Filter *.done -ErrorAction SilentlyContinue)
if ($done.Count -eq 0) {
    Write-Host '  No .done files - the run has not completed any folder yet.' -ForegroundColor Yellow
}
foreach ($file in $done) {
    $owner = (Get-Content $file.FullName -Raw) -replace '^instance=(\S+).*', '$1'
    '{0,-20} -> {1}' -f $file.BaseName, $owner | Write-Host
}

$stale = @(Get-ChildItem -Path $coord -Filter *.claim -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) {
    Write-Host ("  {0} folder(s) still claimed - an instance is running or was killed mid-folder." -f $stale.Count) -ForegroundColor Yellow
}

$folders = @(Get-ChildItem -Path $share -Directory)
Write-Host ''
'  Input folders : {0}' -f $folders.Count | Write-Host
'  Completed     : {0}' -f $done.Count | Write-Host
if ($done.Count -eq $folders.Count) {
    Write-Host '  All folders completed.' -ForegroundColor Green
} else {
    Write-Host ('  {0} folder(s) not completed.' -f ($folders.Count - $done.Count)) -ForegroundColor Yellow
}

# --- 2. Per-instance share of the work --------------------------------------
Head 'Work distribution'
$done | Group-Object { (Get-Content $_.FullName -Raw) -replace '^instance=(\S+).*', '$1' } |
    Sort-Object Name | ForEach-Object {
        '  {0,-14} {1,2} folder(s): {2}' -f $_.Name, $_.Count, (($_.Group | ForEach-Object BaseName) -join ', ') | Write-Host
    }

# --- 3. Files in, files out --------------------------------------------------
Head 'File counts'
$remaining = @(Get-ChildItem -Path $share -Recurse -Filter *.xhtml -File |
    Where-Object { $_.FullName -notmatch '\\(archive|error)\\' })
$archived = @(Get-ChildItem -Path $share -Recurse -Filter *.xhtml -File |
    Where-Object { $_.FullName -match '\\archive\\' })
$errored = @(Get-ChildItem -Path $share -Recurse -Filter *.xhtml -File |
    Where-Object { $_.FullName -match '\\error\\' })
$rtf = @(Get-ChildItem -Path $out -Recurse -Filter *.rtf -File -ErrorAction SilentlyContinue)

'  Unprocessed inputs : {0}' -f $remaining.Count | Write-Host
'  Archived (success) : {0}' -f $archived.Count | Write-Host
'  Error folder       : {0}' -f $errored.Count | Write-Host
'  RTF written        : {0}' -f $rtf.Count | Write-Host

if ($archived.Count -ne $rtf.Count) {
    Write-Host '  NOTE: archived and RTF counts differ - check the error report.' -ForegroundColor Yellow
}

# --- 4. Duplicate processing check ------------------------------------------
# The real proof: every document appears in exactly one instance's file trace.
Head 'Duplicate check (file trace across all instances)'
$trace = @(Get-ChildItem -Path $reports -Recurse -Filter 'file_trace_*.csv' -File -ErrorAction SilentlyContinue)
if ($trace.Count -eq 0) {
    Write-Host '  No file trace found (Processing.EnableFileTrace must be true).' -ForegroundColor Yellow
} else {
    $rows = $trace | ForEach-Object { Import-Csv $_.FullName }
    $keyed = $rows | Group-Object { '{0}|{1}|{2}' -f $_.'Date Folder', $_.'Sub Folder', $_.'File Name' }
    $dupes = @($keyed | Where-Object { $_.Count -gt 1 })

    '  Documents traced : {0}' -f $rows.Count | Write-Host
    if ($dupes.Count -eq 0) {
        Write-Host '  No document was processed more than once.' -ForegroundColor Green
    } else {
        Write-Host ('  {0} document(s) processed more than once:' -f $dupes.Count) -ForegroundColor Red
        $dupes | Select-Object -First 10 | ForEach-Object { '    {0} ({1}x)' -f $_.Name, $_.Count | Write-Host }
    }
}

# --- 5. Failures -------------------------------------------------------------
Head 'Failures'
$errorReports = @(Get-ChildItem -Path $reports -Recurse -Filter 'error_report_*.csv' -File -ErrorAction SilentlyContinue)
if ($errorReports.Count -eq 0) {
    Write-Host '  No error report written.' -ForegroundColor Green
} else {
    $failures = $errorReports | ForEach-Object { Import-Csv $_.FullName }
    '  Failed documents : {0}' -f @($failures).Count | Write-Host
    $failures | Group-Object Reason | Sort-Object Count -Descending |
        ForEach-Object { '    {0,4}x  {1}' -f $_.Count, $_.Name | Write-Host }
}

Write-Host ''
