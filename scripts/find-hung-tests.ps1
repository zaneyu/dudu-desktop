<#
.SYNOPSIS
    Runs one test project class by class (then method by method inside any
    class that does not finish) so a hung test names itself. Diagnostic
    only; the normal suite run is a single `dotnet test`.

.PARAMETER Project
    Path to the test project (.csproj). It must already be built in Release.

.PARAMETER TimeoutSeconds
    How long one class (or one method) may run before it is killed and
    reported as hung.
#>
param(
    [Parameter(Mandatory)] [string]$Project,
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$projectDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Project))

function Invoke-Filtered {
    param([string[]]$FilterArguments, [string]$Label)

    $arguments = @("test", "--project", $Project, "-c", "Release", "--no-build", "--") + $FilterArguments
    $process = Start-Process dotnet -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput ([IO.Path]::Combine([IO.Path]::GetTempPath(), "hung-$([guid]::NewGuid().ToString('N')).log"))
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        Write-Host "HUNG  $Label (no exit within $TimeoutSeconds s)"
        return "hung"
    }
    $status = if ($process.ExitCode -eq 0) { "ok  " } elseif ($process.ExitCode -eq 8) { "none" } else { "FAIL" }
    Write-Host "$status  $Label (exit $($process.ExitCode))"
    return $status.Trim()
}

# Classes and methods come from the sources: xunit's --list-tests output is
# not printed by the MTP console runner, and reflection would need the WinUI
# app's dependencies loaded here.
$classes = @{}
foreach ($file in Get-ChildItem -Path $projectDirectory -Recurse -Filter *.cs) {
    if ($file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
    $text = Get-Content -Raw $file.FullName
    $namespace = [regex]::Match($text, '(?m)^namespace\s+([\w.]+)\s*;').Groups[1].Value
    if (-not $namespace) { continue }
    foreach ($class in [regex]::Matches($text, '(?m)^public (?:sealed |static )?class (\w+)')) {
        $name = $class.Groups[1].Value
        $body = $text.Substring($class.Index)
        $methods = [regex]::Matches($body, '\[(?:Fact|Theory)[^\]]*\][\s\S]*?public (?:async )?\w+(?:<\w+>)? (\w+)\(') |
            ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        if ($methods.Count -gt 0) { $classes["$namespace.$name"] = $methods }
    }
}

$hung = @()
foreach ($class in ($classes.Keys | Sort-Object)) {
    $result = Invoke-Filtered @("--filter-class", $class) $class
    if ($result -eq "hung") { $hung += $class }
}

foreach ($class in $hung) {
    Write-Host "--- methods of $class ---"
    foreach ($method in $classes[$class]) {
        Invoke-Filtered @("--filter-method", "$class.$method") "$class.$method" | Out-Null
    }
}

if ($hung.Count -gt 0) {
    Write-Host "Hung classes: $($hung -join ', ')"
    exit 1
}
Write-Host "No class hung."
exit 0
