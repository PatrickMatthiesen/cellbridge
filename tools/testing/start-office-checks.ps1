# Run from the prepared Windows bundle in a logged-in desktop.
[CmdletBinding()]
param(
    [ValidateSet('Word','Excel')][string]$Application = 'Word',
    [ValidateSet('office-a','office-b')][string]$Account = 'office-a',
    [ValidateRange(2,100)][int]$Cycles = 20,
    [string]$Configuration = (Join-Path $PSScriptRoot 'office-test.json'),
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$CopyOfficePassword
)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
$origin = [uri]$config.origin
if (-not $origin.IsAbsoluteUri -or $origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment) {
    throw 'Configuration must specify an HTTPS origin.'
}
if ($config.candidateCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Configuration must identify the exact candidate.' }
$baseUrl = $config.origin.TrimEnd('/')
$manifestRoot = Split-Path -Parent ([IO.Path]::GetFullPath($Configuration))
foreach ($file in $config.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $manifestRoot $file.path))
    if (-not $path.StartsWith($manifestRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid bundle file path.' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Bundle file changed: $($file.path)" }
}
if (-not $Credential) {
    if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) { throw 'Install Windows OpenSSH Client or supply -Credential.' }
    if ($config.sshTarget -notmatch '^[a-zA-Z0-9_.-]+@[a-zA-Z0-9_.-]+$' -or $config.credentialsFile -notmatch '^/[a-zA-Z0-9_./-]+$') {
        throw 'Invalid SSH credential location.'
    }
    # SSH keeps credentials out of the downloadable bundle and command arguments.
    # Establish and verify the server host key once before running this script.
    $privateJson = (& ssh -T -o BatchMode=yes $config.sshTarget "cat '$($config.credentialsFile)'" | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'SSH credential retrieval failed. Connect to the dev server first, or supply -Credential.' }
    try { $private = $privateJson | ConvertFrom-Json } catch { throw 'Private server configuration is invalid.' }
    $entry = $private.accounts.$Account
    if (-not $entry.password -or $entry.login -ne $Account) { throw 'Test account is absent from private server configuration.' }
    $Credential = New-Object Management.Automation.PSCredential($entry.login, (ConvertTo-SecureString $entry.password -AsPlainText -Force))
    $privateJson = $null; $private = $null; $entry = $null
}
if ($Credential.UserName -ne $Account) { throw 'Credential must match the selected test account.' }
if ($CopyOfficePassword) {
    Set-Clipboard -Value $Credential.GetNetworkCredential().Password
    Write-Host 'Test password copied for the Office sign-in dialog. Clear the clipboard after sign-in.'
}
function Form-Token([string]$Page) {
    $match = [regex]::Match($Page, 'name="__RequestVerificationToken"\s+value="([^"]+)"')
    if (-not $match.Success) { throw 'Login form token is missing.' }
    return [Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
}
$savedCookie = $env:CELLBRIDGE_INTEROP_COOKIE
$savedCsrf = $env:CELLBRIDGE_INTEROP_CSRF
try {
    Invoke-RestMethod "$baseUrl/health" -TimeoutSec 30 | Out-Null
    $page = Invoke-WebRequest "$baseUrl/auth/login" -UseBasicParsing -SessionVariable session -TimeoutSec 30
    $form = @{ login = $Credential.UserName; password = $Credential.GetNetworkCredential().Password;
        __RequestVerificationToken = (Form-Token $page.Content); returnUrl = '/auth/complete' }
    Invoke-WebRequest "$baseUrl/auth/login" -Method Post -Body $form -WebSession $session -UseBasicParsing -TimeoutSec 30 | Out-Null
    $form = $null
    Invoke-RestMethod "$baseUrl/api/documents" -WebSession $session -TimeoutSec 30 | Out-Null
    $page = Invoke-WebRequest "$baseUrl/auth/login" -WebSession $session -UseBasicParsing -TimeoutSec 30
    $env:CELLBRIDGE_INTEROP_COOKIE = $session.Cookies.GetCookieHeader([uri]$baseUrl)
    $env:CELLBRIDGE_INTEROP_CSRF = Form-Token $page.Content
    $output = Join-Path $manifestRoot ('results\' + $env:COMPUTERNAME + '-' + $Account + '-' + $Application + '-' + [guid]::NewGuid().ToString('N'))
    Write-Host "Office signs in separately as $Account. Keep the desktop visible for its dialog."
    & (Join-Path $manifestRoot 'office-repeat.ps1') -BaseUrl $baseUrl -CandidateCommit $config.candidateCommit `
        -Application $Application -Cycles $Cycles -OutputDirectory $output
    if (-not $?) { throw 'Office repeat check failed.' }
    Write-Host "Evidence saved in $output. Manual checks and server capture review remain separate."
} finally {
    $env:CELLBRIDGE_INTEROP_COOKIE = $savedCookie
    $env:CELLBRIDGE_INTEROP_CSRF = $savedCsrf
    if ($CopyOfficePassword -and (Get-Clipboard -Raw) -eq $Credential.GetNetworkCredential().Password) { Set-Clipboard -Value '' }
    $Credential = $null; $session = $null; $form = $null
}
