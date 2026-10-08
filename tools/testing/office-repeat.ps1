# Automated content evidence. Saved UI and close-prompt observations remain manual gates.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$CandidateCommit,
    [ValidateSet('Word','Excel')][string]$Application = 'Word',
    [ValidateRange(1,100)][int]$Cycles = 20,
    [string]$OutputDirectory = (Join-Path $PWD ('artifacts\office-repeat-' + [guid]::NewGuid().ToString('N'))),
    [ValidateRange(60,14400)][int]$TimeoutSeconds = 3600,
    [switch]$Worker,
    [switch]$HttpPreflightOnly
)
$ErrorActionPreference = 'Stop'
foreach ($module in @('Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Utility')) {
    Import-Module (Join-Path $PSHOME "Modules/$module/$module.psd1") -ErrorAction Stop
}
$uri = [uri]$BaseUrl
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'BaseUrl must be an HTTPS origin.' }
if ($CandidateCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Supply the full tested candidate commit.' }
$BaseUrl = $BaseUrl.TrimEnd('/')
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$resultPath = Join-Path $OutputDirectory 'repeat-result.json'
$pidPath = Join-Path $OutputDirectory 'owned-office.pid'
$processName = if ($Application -eq 'Word') { 'WINWORD' } else { 'EXCEL' }
if (-not $Worker) {
    if (-not $HttpPreflightOnly -and (Get-Process WINWORD,EXCEL -ErrorAction SilentlyContinue)) { throw 'Close Word and Excel before running in this desktop.' }
    if (Test-Path $OutputDirectory) { throw 'Choose a new output directory.' }
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    function Quote-PS([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }
    $command = '& ' + (Quote-PS $PSCommandPath) + ' -Worker -BaseUrl ' + (Quote-PS $BaseUrl) +
        ' -CandidateCommit ' + (Quote-PS $CandidateCommit) + ' -Application ' + $Application +
        ' -Cycles ' + $Cycles + ' -OutputDirectory ' + (Quote-PS $OutputDirectory)
    if ($HttpPreflightOnly) { $command += ' -HttpPreflightOnly' }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $started = Get-Date
    $start = @{ FilePath = (Get-Process -Id $PID).Path; PassThru = $true;
        ArgumentList = @('-NoProfile','-EncodedCommand',$encoded);
        WorkingDirectory = (Get-Location).Path;
        RedirectStandardOutput = (Join-Path $OutputDirectory 'stdout.log');
        RedirectStandardError = (Join-Path $OutputDirectory 'stderr.log') }
    if ($env:OS -eq 'Windows_NT') {
        $start.WindowStyle = 'Hidden'
        $start.ArgumentList = @('-NoProfile','-STA','-ExecutionPolicy','RemoteSigned','-EncodedCommand',$encoded)
    } elseif (-not $HttpPreflightOnly) { throw 'Desktop Office checks require Windows.' }
    $child = Start-Process @start
    [void]$child.Handle
    $finished = $child.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path $pidPath) {
        $owned = Get-Process -Id ([int](Get-Content $pidPath -Raw)) -ErrorAction SilentlyContinue
        if ($owned -and $owned.ProcessName -eq $processName -and $owned.StartTime -ge $started.AddSeconds(-2)) { Stop-Process -Id $owned.Id -Force }
    }
    if (-not $finished) { throw "Office timed out. Evidence remains in $OutputDirectory." }
    if ($child.ExitCode -ne 0) { throw "Office worker failed. Inspect $resultPath and stderr.log." }
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    if ($HttpPreflightOnly) {
        if (-not $result.httpPreflightPassed -or $result.officeStarted) { throw "HTTP preflight failed. Inspect $resultPath." }
        $result.workerFinished = $true
        $result | ConvertTo-Json -Depth 12 | Set-Content $resultPath -Encoding UTF8
        Write-Host "Authenticated document creation/download passed: $resultPath. Office was not started."
        exit 0
    }
    if (-not $result.contentCheckPassed -or $result.cleanupError) { throw "Office content or cleanup check failed. Inspect $resultPath." }
    $result.workerFinished = $true
    $result | ConvertTo-Json -Depth 12 | Set-Content $resultPath -Encoding UTF8
    Write-Host "Content run completed: $resultPath. Manual UI observations and capture verification are still required."
    exit 0
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RepeatOfficeWindow {
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
'@
$app = $null
$document = $null
function Release-Com($value) {
    if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value) }
}
function Close-Document {
    if ($script:document) {
        # Never issue a cleanup save. This suppresses prompts and cannot prove their absence.
        try { if ($Application -eq 'Word') { $script:document.Close(0) } else { $script:document.Close($false) } }
        finally { Release-Com $script:document; $script:document = $null }
    }
}
function Stop-Office {
    Close-Document
    if ($script:app) {
        try { $script:app.Quit() } finally { Release-Com $script:app; $script:app = $null }
        if (Test-Path $pidPath) {
            $owned = Get-Process -Id ([int](Get-Content $pidPath -Raw)) -ErrorAction SilentlyContinue
            if ($owned -and -not $owned.WaitForExit(10000)) { throw 'Owned Office process did not exit.' }
        }
    }
}
function Start-Office {
    if (Get-Process $processName -ErrorAction SilentlyContinue) { throw 'An Office process already exists.' }
    $script:app = New-Object -ComObject "$Application.Application"
    $script:app.Visible = $true
    $startup = $null
    $window = $null
    try {
        if ($Application -eq 'Word') {
            $startup = $script:app.Documents.Add()
            $window = $startup.ActiveWindow
            $handle = $window.Hwnd
        } else { $handle = $script:app.Hwnd }
        [uint32]$ownedId = 0
        [void][RepeatOfficeWindow]::GetWindowThreadProcessId([IntPtr]$handle, [ref]$ownedId)
        if ($ownedId -eq 0) { throw 'Cannot determine owned Office process.' }
        $ownedId | Set-Content $pidPath -Encoding ASCII
    } finally {
        Release-Com $window
        if ($startup) { try { $startup.Close(0) } finally { Release-Com $startup } }
    }
    # Preserve BackgroundSave, trust, macro and authentication settings.
}
function Open-Document {
    if ($Application -eq 'Word') { $script:document = $script:app.Documents.Open($documentUrl, $false, $false, $false) }
    else {
        $workbooks = $script:app.Workbooks
        try { $script:document = $workbooks.Open($documentUrl, 0, $false) }
        finally { Release-Com $workbooks }
    }
    if ($script:document.ReadOnly) { throw 'Office opened the document read-only.' }
}
function Invoke-ExcelCell([int]$Row, [string]$Value, [switch]$Read) {
    $worksheets = $worksheet = $cells = $cell = $null
    try {
        $worksheets = $script:document.Worksheets
        $worksheet = $worksheets.Item(1)
        $cells = $worksheet.Cells
        $cell = $cells.Item($Row, 1)
        if ($Read) { return [string]$cell.Value2 }
        $cell.Value2 = $Value
    } finally {
        Release-Com $cell
        Release-Com $cells
        Release-Com $worksheet
        Release-Com $worksheets
    }
}
function Read-Package([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $parts = @()
        $images = @()
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -eq 'word/document.xml' -or $entry.FullName -eq 'xl/sharedStrings.xml' -or $entry.FullName -like 'xl/worksheets/*.xml') {
                $reader = New-Object IO.StreamReader($entry.Open())
                try { [xml]$xml = $reader.ReadToEnd(); $parts += ($xml.SelectNodes("//*[local-name()='t']") | ForEach-Object { $_.InnerText }) -join '' }
                finally { $reader.Dispose() }
            }
            if ($entry.FullName -like 'word/media/*') { $images += $entry.FullName }
        }
        return @{ text = $parts -join "`n"; images = $images.Count }
    } finally { $zip.Dispose() }
}
$runId = [guid]::NewGuid().ToString('N')
$extension = if ($Application -eq 'Word') { 'docx' } else { 'xlsx' }
$fileName = "repeat-$runId.$extension"
$documentUrl = "$BaseUrl/shared/$fileName"
$result = [ordered]@{ schemaVersion = 1; application = $Application; candidateCommit = $CandidateCommit; origin = $BaseUrl;
    scriptSha256 = (Get-FileHash $PSCommandPath -Algorithm SHA256).Hash; documentUrl = $documentUrl;
    expectedCycles = $Cycles; workerFinished = $false; contentCheckPassed = $false; cycles = @();
    httpPreflightPassed = $false; officeStarted = $false; httpPreflightOnly = [bool]$HttpPreflightOnly;
    closePromptObservation = 'manual-required'; startedUtc = [DateTime]::UtcNow.ToString('o') }
try {
    if (-not $env:CELLBRIDGE_INTEROP_COOKIE -or -not $env:CELLBRIDGE_INTEROP_CSRF) { throw 'Supply existing signed-in CELLBRIDGE_INTEROP_COOKIE and CELLBRIDGE_INTEROP_CSRF. Office signs in separately.' }
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    foreach ($cookie in $env:CELLBRIDGE_INTEROP_COOKIE.Split(';')) {
        if ($cookie.Trim()) { $session.Cookies.SetCookies([uri]"$BaseUrl/", $cookie.Trim()) }
    }
    $headers = @{ 'X-CellBridge-CSRF' = $env:CELLBRIDGE_INTEROP_CSRF; 'Cache-Control' = 'no-cache' }
    Invoke-RestMethod "$BaseUrl/api/documents" -WebSession $session -Headers $headers -Method Post -ContentType 'application/json' `
        -Body (@{ name = $fileName; type = $extension } | ConvertTo-Json) -TimeoutSec 30 | Out-Null
    $previousEtag = [string](Invoke-WebRequest $documentUrl -WebSession $session -Headers $headers -Method Head -UseBasicParsing -TimeoutSec 30).Headers['ETag']
    if ($HttpPreflightOnly) {
        $download = Join-Path $OutputDirectory "http-preflight.$extension"
        Invoke-WebRequest $documentUrl -WebSession $session -Headers $headers -UseBasicParsing -TimeoutSec 30 -OutFile $download | Out-Null
        $null = Read-Package $download
        if (-not $previousEtag) { throw 'Created document has no ETag.' }
        $result.httpPreflightPassed = $true
        $result.initialEtag = $previousEtag
        $result.initialSha256 = (Get-FileHash $download -Algorithm SHA256).Hash
        return
    }
    $markers = @()
    $imageCount = 0
    for ($i = 1; $i -le $Cycles; $i++) {
        Start-Office
        $result.officeStarted = $true
        $result.officeVersion = [string]$app.Version
        $result.officeBuild = [string]$app.Build
        if ($Application -eq 'Word') { $result.backgroundSave = [bool]$app.Options.BackgroundSave }
        Open-Document
        $marker = "CellBridge $runId cycle $i"
        $markers += $marker
        $cycle = [ordered]@{ cycle = $i; marker = $marker; editKind = 'text'; saveCalls = 0; saved = $false; reopened = $false; remoteContentVerified = $false }
        $result.cycles += $cycle
        if ($Application -eq 'Word') {
            $document.Content.InsertAfter("`r$marker`r")
            if ($i % 2 -eq 0) {
                $cycle.editKind = 'image'
                $bitmap = New-Object Drawing.Bitmap(96, 64)
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                $imagePath = Join-Path $OutputDirectory "image-$i.png"
                try { $graphics.Clear([Drawing.Color]::FromArgb(255, ($i * 37) % 256, ($i * 67) % 256, ($i * 97) % 256)); $bitmap.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png) }
                finally { $graphics.Dispose(); $bitmap.Dispose() }
                $range = $document.Range($document.Content.End - 1, $document.Content.End - 1)
                try { $shape = $document.InlineShapes.AddPicture($imagePath, $false, $true, $range); Release-Com $shape }
                finally { Release-Com $range }
                $imageCount++
            }
        } else { Invoke-ExcelCell -Row $i -Value $marker }
        $cycle.expectedImages = $imageCount
        $cycle.saveStartedUtc = [DateTime]::UtcNow.ToString('o')
        $cycle.saveCalls = 1
        $document.Save()
        $cycle.saveReturnedUtc = [DateTime]::UtcNow.ToString('o')
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        $download = Join-Path $OutputDirectory "cycle-$i.$extension"
        do {
            $response = Invoke-WebRequest "$documentUrl`?verification=$runId-$i" -WebSession $session -Headers $headers -UseBasicParsing -TimeoutSec 15 -OutFile $download -PassThru
            $package = Read-Package $download
            $cycle.remoteContentVerified = @($markers | Where-Object { -not $package.text.Contains($_) }).Count -eq 0 -and $package.images -ge $imageCount
            $cycle.saved = [bool]$document.Saved
            if (-not ($cycle.remoteContentVerified -and $cycle.saved)) { Start-Sleep -Milliseconds 500 }
        } until (($cycle.remoteContentVerified -and $cycle.saved) -or [DateTime]::UtcNow -ge $deadline)
        $cycle.saveVerifiedUtc = [DateTime]::UtcNow.ToString('o')
        $cycle.etag = [string]$response.Headers['ETag']
        $cycle.sha256 = (Get-FileHash $download -Algorithm SHA256).Hash
        $cycle.packageFile = [IO.Path]::GetFileName($download)
        $cycle.packageImages = $package.images
        if (-not $cycle.saved -or -not $cycle.remoteContentVerified -or -not $cycle.etag -or $cycle.etag -eq $previousEtag) { throw "Cycle $i did not reach Saved with changed remote bytes and ETag." }
        $previousEtag = $cycle.etag
        Stop-Office
        Start-Office
        Open-Document
        if ($Application -eq 'Word') {
            $reopenedText = [string]$document.Content.Text
            $cycle.reopened = @($markers | Where-Object { -not $reopenedText.Contains($_) }).Count -eq 0 -and $document.InlineShapes.Count -ge $imageCount
        } else {
            $cycle.reopened = $true
            for ($row = 1; $row -le $i; $row++) { if ((Invoke-ExcelCell -Row $row -Read) -ne $markers[$row-1]) { $cycle.reopened = $false } }
        }
        $cycle.reopenedUtc = [DateTime]::UtcNow.ToString('o')
        if (-not $cycle.reopened) { throw "Cycle $i failed fresh-process reopen." }
        Stop-Office
        $result | ConvertTo-Json -Depth 12 | Set-Content $resultPath -Encoding UTF8
    }
    $result.contentCheckPassed = $true
} catch { $result.error = $_.Exception.Message; throw }
finally {
    try { Stop-Office } catch { $result.cleanupError = $_.Exception.Message; $result.contentCheckPassed = $false }
    $result.completedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 12 | Set-Content $resultPath -Encoding UTF8
}
