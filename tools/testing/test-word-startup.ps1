# Test startup ownership and cleanup with a fake Word object. Word.Application
# deliberately has no Hwnd; only the temporary document's Window supplies it.
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'word-smoke.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw 'The Word script does not parse.' }
foreach ($name in @('Close-OfficeDocument', 'Quit-OfficeApplication', 'Release-OfficeObject', 'Start-OwnedWord', 'Close-OwnedWord')) {
    $definition = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing startup function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
Add-Type @'
using System;
public static class CellBridgeWordWindow {
    public static uint GetWindowThreadProcessId(IntPtr window, out uint processId) {
        if (window.ToInt64() != 12345) throw new Exception("Unexpected document window handle");
        processId = 731;
        return 1;
    }
}
'@
function Get-Process {
    [CmdletBinding()]
    param([string]$Name, [int]$Id)
}
function New-Object {
    [CmdletBinding()]
    param([string]$ComObject)
    if ($ComObject -ne 'Word.Application') { throw 'Unexpected COM object.' }
    return $script:FakeWord
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cellbridge-word-startup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$pidPath = Join-Path $testRoot 'owned-word.pid'
$document = $null
try {
    foreach ($case in @('success', 'missing-window', 'add-failure')) {
        Remove-Item $pidPath -ErrorAction SilentlyContinue
        $word = $null
        $previousBackgroundSave = $null
        $startupDocument = [pscustomobject]@{
            ActiveWindow = [pscustomobject]@{ Hwnd = $(if ($case -eq 'missing-window') { $null } else { 12345 }) }
            CloseCalls = 0
        }
        $startupDocument | Add-Member -MemberType ScriptMethod -Name Close -Value { param($SaveChanges) $this.CloseCalls++ }
        $documents = [pscustomobject]@{ Document = $startupDocument; Fail = ($case -eq 'add-failure') }
        $documents | Add-Member -MemberType ScriptMethod -Name Add -Value {
            if ($this.Fail) { throw 'Document initialization failed.' }
            return $this.Document
        }
        $script:FakeWord = [pscustomobject]@{
            Documents = $documents; Visible = $false; QuitCalls = 0
            Options = [pscustomobject]@{ BackgroundSave = $true }
        }
        $script:FakeWord | Add-Member -MemberType ScriptMethod -Name Quit -Value { param($SaveChanges) $this.QuitCalls++ }
        $caught = $null
        try { $created = Start-OwnedWord } catch { $caught = $_.Exception.Message }
        if (-not [object]::ReferenceEquals($word, $script:FakeWord)) { throw 'Startup lost ownership of Word.' }
        if ($case -eq 'success') {
            if ($caught) { throw "Word startup failed: $caught" }
            if ((Get-Content $pidPath -Raw).Trim() -ne '731') { throw 'Wrong Word process recorded.' }
            if ($word.Options.BackgroundSave) { throw 'Background save was not disabled for the check.' }
        } elseif (-not $caught) {
            throw "Startup did not report the simulated failure: $case"
        }
        Close-OwnedWord
        if ($script:FakeWord.QuitCalls -ne 1) { throw 'Startup cleanup did not quit the owned Word instance.' }
        if (-not $script:FakeWord.Options.BackgroundSave) { throw 'Cleanup did not restore the save preference.' }
        if ($case -ne 'add-failure' -and $startupDocument.CloseCalls -ne 1) { throw 'Startup document was not closed.' }
        Write-Host "Passed Word startup case: $case"
    }
} finally {
    Remove-Item $testRoot -Recurse -Force
}
