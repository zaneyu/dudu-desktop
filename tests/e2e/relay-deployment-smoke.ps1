<#
.SYNOPSIS
    Probes a deployed Dudu relay and sender page without requiring credentials.

.DESCRIPTION
    This is a safe deployment smoke test: it never registers a device, redeems a code, sends a
    message, or prints response bodies. It proves that the sender asset is live, /v1/* reaches the
    Worker rather than only a static host, unauthenticated routes return the expected boundary
    statuses, and the privacy/security response headers are present.

    Use -AllowHttp only for a local Wrangler instance. Production/staging probes require HTTPS.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [switch]$AllowHttp
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$baseUri = [Uri]$BaseUrl
if ($baseUri.AbsolutePath -ne "/" -or $baseUri.Query -ne "" -or $baseUri.Fragment -ne "") {
    throw "-BaseUrl must be an origin, not a path or query URL."
}
if ($baseUri.Scheme -ne "https" -and (-not $AllowHttp -or $baseUri.Scheme -ne "http")) {
    throw "Deployment smoke requires HTTPS; use -AllowHttp only for local HTTP."
}

$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(10)

function Invoke-Probe {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateSet("GET", "POST")][string]$Method = "GET",
        [string]$Body
    )

    $httpMethod = if ($Method -eq "POST") { [Net.Http.HttpMethod]::Post } else { [Net.Http.HttpMethod]::Get }
    $request = [Net.Http.HttpRequestMessage]::new($httpMethod, [Uri]::new($baseUri, $Path))
    if ($null -ne $Body) {
        $request.Content = [Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, "application/json")
    }
    $response = $client.Send($request)
    [pscustomobject]@{
        Status = [int]$response.StatusCode
        Headers = $response.Headers
        ContentHeaders = $response.Content.Headers
        Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

function Assert-Status {
    param([string]$Name, [object]$Response, [int]$Expected)
    if ($Response.Status -ne $Expected) {
        throw "$Name returned HTTP $($Response.Status), expected $Expected."
    }
    Write-Host "PASS: $Name returned HTTP $Expected"
}

try {
    $sender = Invoke-Probe -Path "/"
    Assert-Status "sender page" $sender 200
    if ($sender.Body -notmatch "Pair privately") {
        throw "sender page did not contain the expected pairing control."
    }

    $notFound = Invoke-Probe -Path "/v1/does-not-exist"
    Assert-Status "Worker route" $notFound 404
    $requiredHeaders = @{
        "Content-Security-Policy" = "default-src 'none'"
        "X-Content-Type-Options" = "nosniff"
        "Referrer-Policy" = "no-referrer"
        "Permissions-Policy" = "camera=(), microphone=(), geolocation=()"
    }
    foreach ($header in $requiredHeaders.GetEnumerator()) {
        $actual = $notFound.Headers.GetValues($header.Key) -join ","
        if ($actual -ne $header.Value) { throw "Worker route header $($header.Key) was '$actual'." }
    }

    Assert-Status "unauthenticated device pairing-code route" `
        (Invoke-Probe -Path "/v1/devices/pairing-code" -Method POST) 401
    Assert-Status "unauthenticated message route" (Invoke-Probe -Path "/v1/messages") 401
    Assert-Status "origin-protected redeem route" `
        (Invoke-Probe -Path "/v1/pairings/redeem" -Method POST -Body '{"code":"00000000"}') 403

    Write-Host "PASS: relay deployment smoke completed without mutating device, pairing, or message state."
    exit 0
}
finally {
    $client.Dispose()
}
