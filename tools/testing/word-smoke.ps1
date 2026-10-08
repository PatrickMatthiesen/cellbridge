# Run in a logged-in Windows desktop with Word installed. The parent process
# supplies a timeout because a blocked Office COM call cannot cancel itself.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [string]$OutputDirectory = (Join-Path $PWD ('artifacts\word-' + [guid]::NewGuid().ToString('N'))),
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 300,
    [ValidateRange(1, 10)][int]$Edits = 2,
    [switch]$Worker
)
$ErrorActionPreference = 'Stop'
$origin = [uri]$BaseUrl
if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/') {
    throw 'BaseUrl must be an HTTPS origin, for example https://dev.example.ts.net.'
}
$BaseUrl = $BaseUrl.TrimEnd('/')
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$resultPath = Join-Path $OutputDirectory 'word-result.json'
$pidPath = Join-Path $OutputDirectory 'owned-word.pid'

if (-not $Worker) {
    if (Get-Process WINWORD -ErrorAction SilentlyContinue) {
        throw 'Close Word before this check, or run it in a dedicated Windows test desktop.'
    }
    if (Test-Path $resultPath) { throw 'Choose a fresh output directory.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Write-Host "Word check output: $OutputDirectory"
    function Quote-PS([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }
    $command = '& ' + (Quote-PS $PSCommandPath) + ' -Worker -BaseUrl ' + (Quote-PS $BaseUrl) +
        ' -OutputDirectory ' + (Quote-PS $OutputDirectory) + ' -Edits ' + $Edits
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $started = Get-Date
    $child = Start-Process "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -PassThru `
        -ArgumentList @('-NoProfile', '-STA', '-ExecutionPolicy', 'RemoteSigned', '-EncodedCommand', $encoded) `
        -WorkingDirectory ($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath('.')) `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'worker.stdout.log') `
        -RedirectStandardError (Join-Path $OutputDirectory 'worker.stderr.log')
    # Keep the native handle open so ExitCode remains available after a short-lived
    # child exits, including when this parent runs in PowerShell 7.
    [void]$child.Handle
    $finished = $child.WaitForExit($TimeoutSeconds * 1000)
    # Clean up only the Word process recorded by this worker. Never kill all Word
    # processes, including any document the user opened after the check started.
    if (Test-Path $pidPath) {
        $owned = Get-Process -Id ([int](Get-Content $pidPath -Raw)) -ErrorAction SilentlyContinue
        if ($owned -and $owned.ProcessName -eq 'WINWORD' -and $owned.StartTime -ge $started.AddSeconds(-2)) {
            Stop-Process -Id $owned.Id -Force
        }
    }
    if (-not $finished) {
        Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
        @{ passed = $false; error = 'Desktop Word timed out; inspect the desktop and worker logs.' } |
            ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'timeout.json') -Encoding UTF8
        throw "Word timed out. Results: $OutputDirectory"
    }
    if ($child.ExitCode -ne 0) {
        $failure = "Worker exit code: $($child.ExitCode)."
        if (Test-Path $resultPath) {
            $workerResult = Get-Content $resultPath -Raw | ConvertFrom-Json
            if ($workerResult.error) { $failure = [string]$workerResult.error }
            elseif ($workerResult.cleanupError) { $failure = [string]$workerResult.cleanupError }
        } else {
            $workerError = Get-Content (Join-Path $OutputDirectory 'worker.stderr.log') -Raw
            if ($workerError -match '^#<\s*CLIXML') {
                try {
                    [xml]$errorXml = $workerError -replace '^#<\s*CLIXML\s*', ''
                    $workerError = ($errorXml.SelectNodes("//*[local-name()='S' and @S='Error']") |
                        ForEach-Object { $_.InnerText -replace '_x000D_', "`r" -replace '_x000A_', "`n" }) -join ''
                } catch { } # Preserve the original stderr if it isn't valid CLIXML.
            }
            if ($workerError) { $failure = $workerError.Trim() }
        }
        throw "Word check failed: $failure`nResults: $OutputDirectory"
    }
    $verifiedResult = Get-Content $resultPath -Raw | ConvertFrom-Json
    $verifiedResult | Add-Member -NotePropertyName workerFinished -NotePropertyValue $true
    $verifiedResult | ConvertTo-Json -Depth 8 | Set-Content $resultPath -Encoding UTF8
    Get-Content $resultPath
    Write-Host 'Content verified. Run verify_office.py on the dev machine to check the Cell capture.'
    exit 0
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CellBridgeWordWindow {
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
'@
$runId = [guid]::NewGuid().ToString('N')
$fileName = "office-smoke-$runId.docx"
$documentUrl = "$BaseUrl/shared/$fileName"
$marker = "CellBridge desktop Word $runId"
$result = [ordered]@{
    scriptRevision = '2026-10-02-local-preflight'
    documentUrl = $documentUrl; marker = $marker; startedUtc = [DateTime]::UtcNow.ToString('o')
    remoteContentVerified = $false; reopenedInWord = $false; timingsMs = [ordered]@{}
    localWordPreflightPassed = $false
    edits = @()
    scope = 'One interactive desktop Word client; server capture verification is a separate required step.'
}
$word = $null
$document = $null
$previousBackgroundSave = $null
function Get-OfficeFileSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '')
    } finally {
        $hash.Dispose()
        $stream.Dispose()
    }
}
function Close-OfficeDocument($Value) {
    # Use the late-bound COM adapter. Managed Office interop ref-object
    # signatures do not describe its argument marshaling.
    $Value.Close(0)
}
function Quit-OfficeApplication($Value) {
    # Our documents have been explicitly closed. Omit all optional arguments.
    $Value.Quit()
}
function Release-OfficeObject($Value) {
    if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
    }
}
function Start-OwnedWord {
    if (Get-Process WINWORD -ErrorAction SilentlyContinue) { throw 'Word is already running.' }
    $application = New-Object -ComObject Word.Application
    # Retain ownership immediately, including if initialization throws before
    # this function returns. The worker's finally block can then quit Word.
    $script:word = $application
    # Visible Office lets the user resolve first-run/account dialogs. Do not alter
    # Protected View, macro policy or trust settings to force a passing result.
    $application.Visible = $true
    # Word exposes Hwnd on Window, unlike Excel's Application.Hwnd. A disposable
    # local document supplies a real window before opening the remote document.
    $startupDocument = $null
    $startupWindow = $null
    try {
        $startupDocument = $application.Documents.Add()
        $startupWindow = $startupDocument.ActiveWindow
        $windowHandle = $startupWindow.Hwnd
        if ($null -eq $windowHandle -or $windowHandle -eq 0) {
            throw 'Word did not create an accessible document window.'
        }
        [uint32]$wordPid = 0
        [void][CellBridgeWordWindow]::GetWindowThreadProcessId([IntPtr]$windowHandle, [ref]$wordPid)
        if ($wordPid -eq 0) { throw 'Could not identify the test Word process.' }
        $wordPid | Set-Content $pidPath -Encoding ASCII
    } finally {
        Release-OfficeObject $startupWindow
        if ($startupDocument) {
            try { Close-OfficeDocument $startupDocument } finally { Release-OfficeObject $startupDocument }
        }
    }
    $script:previousBackgroundSave = $application.Options.BackgroundSave
    $application.Options.BackgroundSave = $false
    return $application
}
function Close-OwnedWord {
    $cleanupFailure = $null
    if ($script:document) {
        try { Close-OfficeDocument $script:document } catch { $cleanupFailure = $_ }
        finally {
            Release-OfficeObject $script:document
            $script:document = $null
        }
    }
    if ($script:word) {
        try {
            try {
                if ($null -ne $script:previousBackgroundSave) {
                    $script:word.Options.BackgroundSave = $script:previousBackgroundSave
                }
            } finally { Quit-OfficeApplication $script:word }
        } catch {
            if (-not $cleanupFailure) { $cleanupFailure = $_ }
        } finally {
            Release-OfficeObject $script:word
            $script:word = $null
        }
        if (Test-Path $pidPath) {
            $closing = Get-Process -Id ([int](Get-Content $pidPath -Raw)) -ErrorAction SilentlyContinue
            if ($closing -and -not $closing.WaitForExit(10000) -and -not $cleanupFailure) {
                throw 'The test Word process did not exit after Quit.'
            }
        }
    }
    if ($cleanupFailure) { throw $cleanupFailure }
}
try {
    $result.phase = 'local-word-preflight'
    # Exercise the actual Word COM lifecycle before creating a remote document.
    # Managed stand-ins cannot establish whether these calls work in Word.
    $word = Start-OwnedWord
    $result.wordVersion = [string]$word.Version
    $result.wordBuild = [string]$word.Build
    Close-OwnedWord
    $result.localWordPreflightPassed = $true

    $result.phase = 'create-remote-document'
    if (-not $env:CELLBRIDGE_INTEROP_COOKIE -or -not $env:CELLBRIDGE_INTEROP_CSRF) {
        throw 'Supply CELLBRIDGE_INTEROP_COOKIE and CELLBRIDGE_INTEROP_CSRF from a signed-in creator account. Office must separately sign in to this origin.'
    }
    $httpSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    foreach ($cookie in $env:CELLBRIDGE_INTEROP_COOKIE.Split(';')) {
        if ($cookie.Trim()) { $httpSession.Cookies.SetCookies([uri]"$BaseUrl/", $cookie.Trim()) }
    }
    $httpHeaders = @{ 'X-CellBridge-CSRF' = $env:CELLBRIDGE_INTEROP_CSRF }
    $body = @{ name = $fileName; type = 'docx' } | ConvertTo-Json
    Invoke-RestMethod "$BaseUrl/api/documents" -WebSession $httpSession -Headers $httpHeaders -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 30 | Out-Null
    $before = Invoke-WebRequest $documentUrl -WebSession $httpSession -Headers $httpHeaders -Method Head -UseBasicParsing -TimeoutSec 30
    $result.initialEtag = [string]$before.Headers['ETag']
    Invoke-WebRequest $documentUrl -WebSession $httpSession -Headers $httpHeaders -UseBasicParsing -TimeoutSec 30 `
        -OutFile (Join-Path $OutputDirectory 'server-initial.docx') | Out-Null
    $result.phase = 'open-remote-document'
    $word = Start-OwnedWord
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $document = $word.Documents.Open($documentUrl, $false, $false, $false)
    $result.timingsMs.open = $timer.Elapsed.TotalMilliseconds
    $result.wordFullName = [string]$document.FullName
    if ($document.ReadOnly) { throw 'Word opened the remote document read-only.' }
    $previousEtag = $result.initialEtag
    $markers = @()
    for ($edit = 1; $edit -le $Edits; $edit++) {
    $result.phase = "save-edit-$edit"
    $editMarker = "$marker edit $edit"
    $markers += $editMarker
    $document.Content.InsertAfter("`r$editMarker`r")
    $timer.Restart()
    $document.Save()
    $editResult = [ordered]@{ edit = $edit; marker = $editMarker; saveCallMs = $timer.Elapsed.TotalMilliseconds }

    # Check the bytes served by CellBridge, independently of Word's Office cache.
    $download = Join-Path $OutputDirectory "server-save-$edit.docx"
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $verificationHeaders = $httpHeaders.Clone()
        $verificationHeaders['Cache-Control'] = 'no-cache'
        $response = Invoke-WebRequest "$documentUrl`?verification=$runId" -WebSession $httpSession -Headers $verificationHeaders -UseBasicParsing -TimeoutSec 15 `
            -OutFile $download -PassThru
        $zip = [IO.Compression.ZipFile]::OpenRead($download)
        try {
            $entry = $zip.GetEntry('word/document.xml')
            if (-not $entry) { throw 'The server returned a DOCX without word/document.xml.' }
            $reader = New-Object IO.StreamReader($entry.Open())
            try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $text = ($xml.SelectNodes("//*[local-name()='t']") | ForEach-Object { $_.InnerText }) -join ''
            $result.remoteContentVerified = @($markers | Where-Object { -not $text.Contains($_) }).Count -eq 0
        } finally { $zip.Dispose() }
        if (-not $result.remoteContentVerified) { Start-Sleep -Milliseconds 1000 }
    } until ($result.remoteContentVerified -or [DateTime]::UtcNow -ge $deadline)
    if (-not $result.remoteContentVerified) { throw 'Word did not publish the edited text to the server.' }
    $result.savedEtag = [string]$response.Headers['ETag']
    if ($result.savedEtag -eq $previousEtag) { throw "The server ETag did not change after edit $edit." }
    $editResult.etag = $result.savedEtag
    $editResult.saveAndRemoteVerificationMs = $timer.Elapsed.TotalMilliseconds
    $editResult.sha256 = Get-OfficeFileSha256 $download
    $result.edits += $editResult
    $previousEtag = $result.savedEtag
    }
    Copy-Item $download (Join-Path $OutputDirectory 'server-saved.docx')
    $result.phase = 'close-after-edits'
    Close-OwnedWord

    # Start a fresh Word process for reopen. The independent GET above is still
    # required because Office can use a disk cache across process lifetimes.
    $result.phase = 'reopen-remote-document'
    $word = Start-OwnedWord
    $timer.Restart()
    $document = $word.Documents.Open($documentUrl, $false, $false, $false)
    $result.timingsMs.reopen = $timer.Elapsed.TotalMilliseconds
    $reopenedText = [string]$document.Content.Text
    $result.reopenedInWord = @($markers | Where-Object { -not $reopenedText.Contains($_) }).Count -eq 0
    if (-not $result.reopenedInWord) { throw 'Word reopened a document without the saved edit.' }
    $result.contentCheckPassed = $true
} catch {
    $result.contentCheckPassed = $false
    $result.error = $_.Exception.Message
    $result.errorType = $_.Exception.GetType().FullName
    $result.errorHResult = $_.Exception.HResult
    $result.scriptStackTrace = $_.ScriptStackTrace
    throw
} finally {
    $result.completedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 8 | Set-Content $resultPath -Encoding UTF8
    try { Close-OwnedWord } catch {
        $result.cleanupError = $_.Exception.Message
        $result.cleanupScriptStackTrace = $_.ScriptStackTrace
        $result | ConvertTo-Json -Depth 8 | Set-Content $resultPath -Encoding UTF8
        if ($result.contentCheckPassed) { throw }
        Write-Warning "Word cleanup failed: $($result.cleanupError)"
    }
    if ($result.contentCheckPassed) {
        $result.phase = 'complete'
        $result | ConvertTo-Json -Depth 8 | Set-Content $resultPath -Encoding UTF8
    }
}
