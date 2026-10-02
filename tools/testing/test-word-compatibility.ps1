# Test hashing and cleanup control flow without Office. These managed stand-ins
# do not reproduce native COM dispatch or validate the Word calls on Windows.
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'word-smoke.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw 'The Word script does not parse.' }
foreach ($name in @('Get-OfficeFileSha256', 'Close-OfficeDocument', 'Quit-OfficeApplication',
                    'Release-OfficeObject', 'Close-OwnedWord')) {
    $definition = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing compatibility helper: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
Add-Type @'
using System;
public class OfficeCloseProbe {
    public int CloseCalls;
    public bool FailClose;
    public void Close(int saveChanges) {
        if (saveChanges != 0) throw new Exception("Cleanup must discard changes");
        CloseCalls++;
        if (FailClose) throw new InvalidOperationException("Document close failed");
    }
}
public class OfficeOptionsProbe { public bool BackgroundSave; }
public class OfficeQuitProbe {
    public int QuitCalls;
    public OfficeOptionsProbe Options = new OfficeOptionsProbe();
    public void Quit() {
        QuitCalls++;
    }
}
'@
function Get-FileHash { throw 'Get-FileHash is unavailable in this test.' }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cellbridge-word-compatibility-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$pidPath = Join-Path $testRoot 'no-owned-process.pid'
try {
    $file = Join-Path $testRoot 'hash-input.bin'
    [IO.File]::WriteAllBytes($file, [Text.Encoding]::ASCII.GetBytes('abc'))
    $expected = 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD'
    if ((Get-OfficeFileSha256 $file) -ne $expected) { throw 'SHA-256 differs from the known test vector.' }
    # Renaming immediately also checks that hashing closed its input stream.
    Move-Item $file (Join-Path $testRoot 'closed-hash-input.bin')
    Write-Host 'Passed SHA-256 without Get-FileHash.'
    foreach ($failClose in @($false, $true)) {
        $closeProbe = [OfficeCloseProbe]::new()
        $closeProbe.FailClose = $failClose
        $quitProbe = [OfficeQuitProbe]::new()
        $document = $closeProbe
        $word = $quitProbe
        $previousBackgroundSave = $true
        $caught = $null
        try { Close-OwnedWord } catch { $caught = $_.Exception.Message }
        if ($failClose -and (-not $caught -or -not $caught.Contains('Document close failed'))) {
            throw 'Cleanup lost the document close error.'
        }
        if (-not $failClose -and $caught) { throw "Cleanup failed: $caught" }
        if ($closeProbe.CloseCalls -ne 1 -or $quitProbe.QuitCalls -ne 1) { throw 'Cleanup did not close and quit.' }
        if (-not $quitProbe.Options.BackgroundSave) { throw 'Cleanup did not restore the save preference.' }
        if ($null -ne $document -or $null -ne $word) { throw 'Cleanup did not release its references.' }
        Write-Host "Passed cleanup control flow without COM; document close failure: $failClose"
    }
} finally {
    Remove-Item $testRoot -Recurse -Force
}
