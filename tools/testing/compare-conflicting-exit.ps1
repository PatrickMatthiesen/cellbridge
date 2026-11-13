# Synthetic lock probe. This script does not launch or emulate Office.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$TestDocumentUrl,
    [Parameter(Mandatory)][ValidateSet('exclusive','shared-one','shared-two')][string]$Case,
    [Parameter(Mandatory)][ValidateSet('SharePointSE','CellBridge')][string]$ServerProduct,
    [Parameter(Mandatory)][string]$ServerBuild,
    [Parameter(Mandatory)][string]$ServerBuildSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Owner','Outsider')][string]$ExitClientIdentity = 'Owner',
    [switch]$TestDocumentConfirmed,
    [switch]$AllowLoopbackHttp
)
$ErrorActionPreference = 'Stop'
if (-not $TestDocumentConfirmed) { throw 'Confirm that this is an authorized disposable test document with -TestDocumentConfirmed.' }
if (-not $TestDocumentUrl.IsAbsoluteUri -or $TestDocumentUrl.UserInfo -or $TestDocumentUrl.Query -or $TestDocumentUrl.Fragment -or
    ($TestDocumentUrl.Scheme -ne 'https' -and -not ($AllowLoopbackHttp -and $TestDocumentUrl.Scheme -eq 'http' -and $TestDocumentUrl.IsLoopback))) {
    throw 'Use an HTTPS document URL without credentials, query or fragment. HTTP requires an explicit loopback exception.'
}
if ([string]::IsNullOrWhiteSpace($ServerBuild) -or [string]::IsNullOrWhiteSpace($ServerBuildSource)) { throw 'Record the verified server build and its source; HTTP headers are insufficient.' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new, private output directory under an access-controlled parent.' }
# On Windows, prepare a restricted parent ACL before running. No ACL/trust/proxy changes here.
if ($env:OS -ne 'Windows_NT') {
    $parent = Split-Path -Parent $OutputDirectory
    if (-not (Test-Path -LiteralPath $parent)) { throw 'Create an access-controlled output parent first.' }
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
if ($env:OS -ne 'Windows_NT') { & chmod 700 -- $OutputDirectory; if ($LASTEXITCODE -ne 0) { throw 'Could not restrict output directory.' } }
Add-Type -AssemblyName System.Net.Http
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$handler.UseDefaultCredentials = $ServerProduct -eq 'SharePointSE'
$http = [Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(30)
if ($ServerProduct -eq 'CellBridge') {
    if ([string]::IsNullOrWhiteSpace($env:CELLBRIDGE_INTEROP_COOKIE)) { throw 'Supply CELLBRIDGE_INTEROP_COOKIE privately for the existing authorized test account.' }
    $http.DefaultRequestHeaders.TryAddWithoutValidation('Cookie', $env:CELLBRIDGE_INTEROP_COOKIE) | Out-Null
}
$endpoint = [uri]::new($TestDocumentUrl.GetLeftPart([UriPartial]::Authority) + '/_vti_bin/cellstorage.svc')
$ns = 'http://schemas.microsoft.com/sharepoint/soap/'
$rows = [Collections.Generic.List[object]]::new()
$token = 0
$schema = [guid]::NewGuid().ToString('D')
$foreignSchema = [guid]::NewGuid().ToString('D')
$owner = [guid]::NewGuid().ToString('D')
$second = [guid]::NewGuid().ToString('D')
$outsider = [guid]::NewGuid().ToString('D')
$exclusive = [guid]::NewGuid().ToString('D')
$contenderExclusive = [guid]::NewGuid().ToString('D')
$exitClient = if ($ExitClientIdentity -eq 'Owner') { $owner } else { $outsider }
$cleanupTransportFailures = 0
$attemptedOwner = $false; $attemptedSecond = $false; $attemptedContender = $false
$complete = $false
function Send-Lock([string]$Operation, [string]$Client, [string]$SchemaId, [string]$ExclusiveId = '') {
    $script:token++
    $type = if ($ExclusiveId) { 'ExclusiveLock' } else { 'Coauth' }
    $key = if ($ExclusiveId) { 'ExclusiveLockRequestType' } else { 'CoauthRequestType' }
    $xml = [xml]'<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body /></s:Envelope>'
    $body = $xml.DocumentElement.FirstChild
    $version = $xml.CreateElement('RequestVersion', $ns); $version.SetAttribute('Version','2'); $version.SetAttribute('MinorVersion','2'); [void]$body.AppendChild($version)
    $collection = $xml.CreateElement('RequestCollection', $ns); $collection.SetAttribute('CorrelationId',[guid]::NewGuid().ToString('D')); [void]$body.AppendChild($collection)
    $request = $xml.CreateElement('Request', $ns); $request.SetAttribute('Url',$TestDocumentUrl.AbsoluteUri); $request.SetAttribute('RequestToken',[string]$script:token); [void]$collection.AppendChild($request)
    $sub = $xml.CreateElement('SubRequest', $ns); $sub.SetAttribute('Type',$type); $sub.SetAttribute('SubRequestToken',[string]$script:token); [void]$request.AppendChild($sub)
    $data = $xml.CreateElement('SubRequestData', $ns); $data.SetAttribute($key,$Operation); $data.SetAttribute('ClientID',$Client); $data.SetAttribute('SchemaLockID',$SchemaId); $data.SetAttribute('Timeout','300')
    if ($ExclusiveId) { $data.SetAttribute('ExclusiveLockID',$ExclusiveId) }; [void]$sub.AppendChild($data)
    $prefix = Join-Path $OutputDirectory ('{0:D3}-{1}' -f $script:token,$Operation)
    $bytes = [Text.Encoding]::UTF8.GetBytes($xml.OuterXml)
    [IO.File]::WriteAllBytes($prefix + '.request.xml',$bytes)
    $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,$endpoint)
    $message.Content = [Net.Http.ByteArrayContent]::new($bytes)
    $message.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/xml; charset=utf-8')
    [void]$message.Headers.TryAddWithoutValidation('SOAPAction','"http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest"')
    $started = [DateTime]::UtcNow
    try {
        $response = $http.SendAsync($message).GetAwaiter().GetResult()
        try {
            $raw = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            [IO.File]::WriteAllBytes($prefix + '.response.xml',$raw)
            $row = [ordered]@{ token=$script:token; operation=$Operation; client=$Client; schema=$SchemaId; exclusive=$ExclusiveId; utc=$started.ToString('o'); http=[int]$response.StatusCode; error=$null; status=$null }
            $rows.Add($row)
            if (-not $response.IsSuccessStatusCode) { throw "HTTP failure at operation $script:token; inspect private evidence." }
            $reply = [xml][Text.Encoding]::UTF8.GetString($raw)
            $manager = [Xml.XmlNamespaceManager]::new($reply.NameTable); $manager.AddNamespace('p',$ns)
            $parts = $reply.SelectNodes('//p:Response/p:SubResponse',$manager)
            if ($parts.Count -ne 1 -or $parts[0].GetAttribute('SubRequestToken') -ne [string]$script:token -or $parts[0].ParentNode.GetAttribute('RequestToken') -ne [string]$script:token) { throw 'Uncorrelated SOAP response.' }
            $row.error = $parts[0].GetAttribute('ErrorCode')
            $detail = $parts[0].SelectSingleNode('p:SubResponseData',$manager)
            if ($detail) { $row.status = $detail.GetAttribute('CoauthStatus') }
            return [pscustomobject]$row
        } finally { $response.Dispose() }
    } finally { $message.Dispose() }
}
function Require-Success($Reply, [string]$Status = '') {
    if ($Reply.error -ne 'Success' -or ($Status -and $Reply.status -ne $Status)) { throw 'Setup or ownership check failed; this is not a valid conflicting-lock comparison.' }
}
try {
    if ($Case -eq 'exclusive') {
        $attemptedOwner = $true
        Require-Success (Send-Lock 'GetLock' $owner $schema $exclusive)
    } else {
        $attemptedOwner = $true
        Require-Success (Send-Lock 'JoinCoauthoring' $owner $schema) 'Alone'
        if ($Case -eq 'shared-two') {
            $attemptedSecond = $true
            Require-Success (Send-Lock 'JoinCoauthoring' $second $schema) 'Coauthoring'
        }
    }
    # These are injected synthetic Exit requests, never attributed to native Office.
    $exit = Send-Lock 'ExitCoauthoring' $exitClient $foreignSchema
    $retry = Send-Lock 'ExitCoauthoring' $exitClient $foreignSchema
    # Observe membership and fencing before refresh, which can reacquire a lost lease.
    if ($Case -eq 'exclusive') {
        $attemptedContender = $true
        $blocked = Send-Lock 'GetLock' $outsider $foreignSchema $contenderExclusive
    } else {
        $expectedStatus = if ($Case -eq 'shared-two') { 'Coauthoring' } else { 'Alone' }
        Require-Success (Send-Lock 'GetCoauthoringStatus' $owner $schema) $expectedStatus
        if ($Case -eq 'shared-two') { Require-Success (Send-Lock 'GetCoauthoringStatus' $second $schema) $expectedStatus }
        $attemptedContender = $true
        $blocked = Send-Lock 'JoinCoauthoring' $outsider $foreignSchema
    }
    if ($blocked.error -ne 'FileAlreadyLockedOnServer') { throw 'Contender was not fenced; inspect lock ownership before another trial.' }
    if ($Case -eq 'exclusive') {
        Require-Success (Send-Lock 'RefreshLock' $owner $schema $exclusive)
    } else {
        Require-Success (Send-Lock 'RefreshCoauthoring' $owner $schema) $expectedStatus
        if ($Case -eq 'shared-two') { Require-Success (Send-Lock 'RefreshCoauthoring' $second $schema) $expectedStatus }
    }
    $complete = $true
} finally {
    # Release only unpredictable IDs generated by this invocation. A timeout may
    # have committed an acquisition, so clean up attempted acquisitions too.
    $cleanups = [Collections.Generic.List[scriptblock]]::new()
    if ($attemptedContender) {
        $cleanups.Add({
            if ($Case -eq 'exclusive') { Send-Lock 'ReleaseLock' $outsider $foreignSchema $contenderExclusive | Out-Null }
            else { Send-Lock 'ExitCoauthoring' $outsider $foreignSchema | Out-Null }
        })
    }
    if ($attemptedSecond) { $cleanups.Add({ Send-Lock 'ExitCoauthoring' $second $schema | Out-Null }) }
    if ($attemptedOwner) {
        $cleanups.Add({
            if ($Case -eq 'exclusive') { Send-Lock 'ReleaseLock' $owner $schema $exclusive | Out-Null }
            else { Send-Lock 'ExitCoauthoring' $owner $schema | Out-Null }
        })
    }
    try {
        foreach ($cleanup in $cleanups) {
            try { & $cleanup } catch { $script:cleanupTransportFailures++; Write-Warning 'One cleanup request failed; inspect private evidence and the test file.' }
        }
    } finally {
        [ordered]@{ kind='synthetic-conflicting-exit'; case=$Case; exitClientIdentity=$ExitClientIdentity; cleanupTransportFailures=$cleanupTransportFailures; serverProduct=$ServerProduct; serverBuild=$ServerBuild; serverBuildSource=$ServerBuildSource; document=$TestDocumentUrl.AbsoluteUri; nativeOfficeRun=$false; ownershipChecksCompleted=$complete; operations=$rows } |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding utf8
        $http.Dispose()
    }
}
Write-Host "Synthetic probe completed. Inspect Exit responses and ownership checks in $OutputDirectory. Native Office behavior was not tested."
