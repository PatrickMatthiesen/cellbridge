# Exercise the parent wrapper without launching Office or Windows PowerShell.
# These cases reproduce a different PowerShell/process directory and a worker
# blocked before it can create word-result.json.
$ErrorActionPreference = 'Stop'
$scriptUnderTest = Join-Path $PSScriptRoot 'word-smoke.ps1'
$originalLocation = Get-Location
$originalProcessDirectory = [Environment]::CurrentDirectory
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cellbridge-word-wrapper-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Get-Process {
    [CmdletBinding()]
    param([string]$Name, [int]$Id)
    # No pre-existing or owned Word process in this test.
}

function Start-Process {
    [CmdletBinding()]
    param([string]$FilePath, [switch]$PassThru, [string[]]$ArgumentList,
          [string]$WorkingDirectory, [string]$RedirectStandardOutput, [string]$RedirectStandardError)
    $policyIndex = [Array]::IndexOf($ArgumentList, '-ExecutionPolicy')
    if ($policyIndex -lt 0 -or $ArgumentList[$policyIndex + 1] -ne 'RemoteSigned') {
        throw 'The worker must specify its process execution policy.'
    }
    if ($WorkingDirectory -ne (Get-Location).Path) { throw 'Worker working directory differs from PowerShell location.' }
    '' | Set-Content $RedirectStandardOutput
    if ((Split-Path $RedirectStandardError).EndsWith('evidence-stderr')) {
        @'
#< CLIXML
<Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><S S="Error">Worker blocked by execution policy._x000D__x000A_</S></Objs>
'@ | Set-Content $RedirectStandardError
    } else {
        'Fallback stderr' | Set-Content $RedirectStandardError
        $workerResult = if ((Split-Path $RedirectStandardError).EndsWith('evidence-cleanup')) {
            @{ cleanupError = 'Word quit failed.' }
        } else { @{ error = 'Word opened the remote document read-only.' } }
        $workerResult | ConvertTo-Json |
            Set-Content (Join-Path (Split-Path $RedirectStandardError) 'word-result.json')
    }
    $child = [pscustomobject]@{ ExitCode = 1; Handle = [IntPtr]::Zero }
    $child | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($Milliseconds) return $true }
    return $child
}

try {
    Set-Location $testRoot
    # Set-Location does not normally change this .NET process property.
    [Environment]::CurrentDirectory = $originalProcessDirectory
    foreach ($case in @('stderr', 'result', 'cleanup')) {
        $expectedMessage = if ($case -eq 'stderr') { 'Worker blocked by execution policy.' }
                           elseif ($case -eq 'cleanup') { 'Word quit failed.' }
                           else { 'Word opened the remote document read-only.' }
        $caught = $null
        try {
            & $scriptUnderTest -BaseUrl 'https://office-test.example.ts.net' -OutputDirectory ('.\evidence-' + $case)
        } catch {
            $caught = $_.Exception.Message
        }
        if (-not $caught -or -not $caught.Contains($expectedMessage)) {
            throw "Wrapper failed to report the worker error: $caught"
        }
        $expectedDirectory = Join-Path $testRoot ('evidence-' + $case)
        if (-not (Test-Path (Join-Path $expectedDirectory 'worker.stderr.log'))) {
            throw "Wrapper wrote evidence outside the PowerShell location: $caught"
        }
        if ($case -eq 'stderr' -and ($caught.Contains('CLIXML') -or $caught.Contains('_x000D_'))) {
            throw 'Wrapper did not decode the serialized worker error.'
        }
        Write-Host "Passed Word wrapper failure case: $case"
    }
} finally {
    [Environment]::CurrentDirectory = $originalProcessDirectory
    Set-Location $originalLocation
    Remove-Item $testRoot -Recurse -Force
}
