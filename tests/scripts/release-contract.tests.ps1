<#
.SYNOPSIS
    Cross-platform contract checks for release/installer hardening.

.DESCRIPTION
    These checks do not run Windows installers. They verify the maintained pins and the source
    contracts that are easy to regress in a text-only review: version forwarding, private asset
    preflight, isolated smoke-test paths, absence of image-wide process killing, and the pinned
    Inno Setup digest mechanism.
#>
$ErrorActionPreference = "Stop"

# The vendored PowerShell used by the Mac-safe release checks does not always
# auto-import the filesystem and utility modules before the first cmdlet call.
Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
Import-Module Microsoft.PowerShell.Management -ErrorAction SilentlyContinue

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$script:FailureCount = 0
$script:CaseCount = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    $script:CaseCount++
    if ($Condition) { Write-Host "PASS: $Name" } else { Write-Host "FAIL: $Name"; $script:FailureCount++ }
}

function Assert-Contains {
    param([string]$Name, [string]$Text, [string]$Pattern)
    Assert-True $Name ($Text -match $Pattern)
}

$pinPath = Join-Path $repoRoot "installer/inno-setup-pinned.json"
$pin = Get-Content -Raw -LiteralPath $pinPath | ConvertFrom-Json
Assert-True "Inno pin has the maintained 7.1.0 version" ($pin.version -eq "7.1.0")
Assert-True "Inno pin uses the expected x64 filename" ($pin.fileName -eq "innosetup-7.1.0-x64.exe")
Assert-True "Inno pin has a 64-character SHA-256 digest" ($pin.sha256 -match '^[0-9a-f]{64}$')
Assert-True "Inno pin uses an HTTPS GitHub release URL" ($pin.url -match '^https://github\.com/.+/releases/download/.+/' + [Regex]::Escape($pin.fileName) + '$')

$iss = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "installer/DuduDesktop.iss")
$publish = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "scripts/publish-windows.ps1")
$verify = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "scripts/verify.ps1")
$e2e = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "tests/e2e/private-note-flow.ps1")
$workflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".github/workflows/windows-installer.yml")
$productionWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot ".github/workflows/windows-store-production.yml")
$smoke = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "tests/installer/installer-smoke.ps1")
$innoScript = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "scripts/install-inno-setup.ps1")
$appProject = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/Dudu.App/Dudu.App.csproj")
$appExecutableManifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/Dudu.App/app.manifest")
$storeScriptPath = Join-Path $repoRoot "scripts/package-store.ps1"
$packageManifestPath = Join-Path $repoRoot "src/Dudu.App/Package.appxmanifest"
$packageManifestSource = Get-Content -Raw -LiteralPath $packageManifestPath
$readme = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "README.md")
$releaseDoc = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "docs/release.md")
$acceptanceDoc = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "docs/testing/windows-acceptance.md")
$changelog = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "CHANGELOG.md")
$releaseDocs = @($readme, $releaseDoc, $acceptanceDoc, $changelog, $workflow)

$officialMicrosoftDocumentationUrlPattern = '(?i)https?://(?:learn|support)\.microsoft\.com(?:[/ :]|$)'
$concreteServiceUrlPattern = '(?i)https?://(?!<[^>]+>)(?!learn\.microsoft\.com(?:[/ :]|$))(?!support\.microsoft\.com(?:[/ :]|$))(?!keepachangelog\.com(?:[/ :]|$))[^\s)>]+'
$privacyUrlFixtures = @('https://relay.example.invalid/v1', 'https://sender.example.invalid/')

Assert-Contains "release runbook documents private Store submission" $releaseDoc "Partner Center"
Assert-Contains "release runbook documents private Store audience" $releaseDoc "private-audience|audience.*private"
Assert-Contains "release runbook documents Store hash comparison" $releaseDoc "SHA-256 hash"
Assert-Contains "release runbook names Store artifact" $releaseDoc "DuduDesktop-<version>-win-x64-store"
Assert-Contains "release runbook names Store hash metadata" $releaseDoc "store-package-metadata/SHA256SUMS\.txt"
Assert-Contains "release runbook names Store version metadata" $releaseDoc "package-version\.txt"
Assert-Contains "release runbook documents Store certification" $releaseDoc "certification"
Assert-Contains "release runbook documents Store publication signing" $releaseDoc "Microsoft-signed"
Assert-Contains "release runbook documents Inno migration fallback" $releaseDoc "two\s+Store versions"
Assert-Contains "release runbook documents package identity migration risk" $releaseDoc "(?s)startup shortcuts.*notifications.*activation.*uninstall"
Assert-Contains "release runbook marks the CI Store artifact acceptance-only" $releaseDoc "(?is)CI artifact.*acceptance-only"
Assert-Contains "release runbook forbids uploading the CI Store artifact" $releaseDoc "(?is)never upload.*CI artifact|CI artifact.*never upload"
Assert-Contains "release runbook requires a separate local gated package" $releaseDoc "identity-gated\s+local\s+command"
Assert-Contains "release runbook uploads only the local gated package" $releaseDoc "Upload only that locally produced"
Assert-Contains "release runbook identifies the restricted capability" $releaseDoc "rescap:unvirtualizedResources"
Assert-Contains "release runbook requires private restricted-capability approval" $releaseDoc "(?is)restricted-capability.*justification/approval.*retained privately"
Assert-Contains "release runbook distinguishes WACK PASS from Store approval" $releaseDoc "(?is)WACK.*PASS.*not Store approval"
Assert-Contains "desktop app embeds a native DPI manifest" $appProject "ApplicationManifest.*app\.manifest"
Assert-Contains "native DPI manifest enables PerMonitorV2" $appExecutableManifest "dpiAwareness.*PerMonitorV2"
Assert-True "manifest restricted capability has a documented Partner Center approval gate" (
    ($packageManifestSource -notmatch 'rescap:Capability\s+Name="unvirtualizedResources"') -or
    ($releaseDoc -match '(?is)rescap:unvirtualizedResources.*restricted-capability.*justification/approval.*retained privately')
)
Assert-Contains "README links Store release path" $readme "private Store"
Assert-Contains "acceptance matrix includes Store visibility" $acceptanceDoc "Store visibility"
Assert-Contains "acceptance matrix includes second Store update" $acceptanceDoc "second Store update"
Assert-Contains "changelog records private Store migration" $changelog "private .*Store"
Assert-True "concrete relay and sender URLs are detected generally" (@($privacyUrlFixtures | Where-Object { $_ -match $concreteServiceUrlPattern }).Count -eq 2)
Assert-True "official Microsoft documentation links are allowed" ('https://learn.microsoft.com/windows/apps/' -notmatch $concreteServiceUrlPattern)
Assert-True "release sources contain no concrete relay or sender URL" (@($releaseDocs | Where-Object { $_ -match $concreteServiceUrlPattern }).Count -eq 0)
Assert-True "release sources contain no pairing-code literal" (@($releaseDocs | Where-Object { $_ -match '(?i)pairing\s+code\s*[:=]\s*[0-9A-Z-]{6,}' }).Count -eq 0)
Assert-True "release sources contain no embedded token value" (@($releaseDocs | Where-Object { $_ -match '(?i)(?:access[_ -]?token|authentication[_ -]?token|bearer)\s*[:=]\s*[A-Za-z0-9._~+/=-]{12,}' }).Count -eq 0)
Assert-True "release sources contain no private-key material" (@($releaseDocs | Where-Object { $_ -match 'BEGIN (?:EC|RSA|OPENSSH) PRIVATE KEY|PRIVATE KEY-----' }).Count -eq 0)
Assert-True "release sources contain no raw-artwork path" (@($releaseDocs | Where-Object { $_ -match 'assets[/\\]raw[/\\]' }).Count -eq 0)

Assert-True "Store package script exists" (Test-Path -LiteralPath $storeScriptPath)
if (Test-Path -LiteralPath $storeScriptPath) {
    $storeScript = Get-Content -Raw -LiteralPath $storeScriptPath
    Assert-Contains "Store package script requires Windows" $storeScript "OperatingSystem.*Windows"
    Assert-Contains "Store package script enables the package mode" $storeScript "DuduStorePackage.*true"
    Assert-Contains "Store package publish explicitly requests MSIX generation" $storeScript "GenerateAppxPackageOnBuild.*true"
    Assert-Contains "Store package script targets win-x64" $storeScript "RuntimeIdentifier.*win-x64"
    Assert-Contains "Store package script disables local signing" $storeScript "AppxPackageSigningEnabled.*false"
    Assert-Contains "Store package script writes beneath artifacts" $storeScript "artifacts[/\\]store-package"
    Assert-True "Store package script does not publish a relay URL" ($storeScript -notmatch "workers\.dev|DUDU_RELAY_BASE_URL")
    Assert-True "Store package script does not embed credentials" ($storeScript -notmatch 'clientSecret|accessToken|password\s*=\s*[''\"][^''\"]+|token\s*=\s*[''\"][^''\"]+')
    Assert-Contains "Store package script exposes strict package-output validation" $storeScript "Assert-StrictStorePackageOutput"
    Assert-True "Store package script removes bundle-unpack complexity" ($storeScript -notmatch "makeAppx unbundle")
    Assert-Contains "Store package script validates Store version component bounds" $storeScript "Assert-StoreVersion"
    Assert-Contains "Store package script limits Store version components to 65535" $storeScript "65535"
    Assert-Contains "Store package script fixes the fourth Store version component to zero" $storeScript 'expectedPackageVersion.*\$Version\.0'
    Assert-Contains "Store package script validates the Store package version" $storeScript "Store package version must use Major\.Minor\.Patch"
    Assert-Contains "Store package script requires Windows build 26100" $storeScript "OSVersion.*Build.*26100"
    Assert-Contains "Store package script requires a 64-bit OS" $storeScript "Is64BitOperatingSystem"
    Assert-Contains "Store package script requires the pinned SDK" $storeScript "dotnetVersion.*10\.0\.112"
    Assert-Contains "Store package script fixes the target runtime to win-x64" $storeScript "targetRuntimeIdentifier.*win-x64"
    Assert-Contains "Store package publish uses the verified no-restore path" $storeScript "dotnet publish.*--no-restore"
    Assert-Contains "Store package script requires restored assets before publishing" $storeScript "project\.assets\.json"
    Assert-True "Store package script does not recursively select arbitrary SDK tools" ($storeScript -notmatch "function Find-Tool")
    Assert-Contains "Store package script resolves SDK tools from the Windows Kits root" $storeScript "Windows Kits\\10"
    Assert-Contains "Store package script enumerates versioned Windows SDK bin directories" $storeScript "Get-ChildItem -LiteralPath \`$sdkBinRoot -Directory"
    Assert-Contains "Store package script resolves an x64 MakeAppx executable" $storeScript "x64.*makeappx\.exe"
    Assert-Contains "Store package script requires Windows SDK 26100 or newer for package tools" $storeScript "10\.0\.26100\.0"
    Assert-Contains "Store package script resolves AppCert from the selected Windows SDK root" $storeScript "App Certification Kit.*appcert\.exe"
    Assert-Contains "Store package script rejects an ambiguous SDK tool resolution" $storeScript "ambiguous"
    Assert-Contains "Store package script exposes the production identity gate" $storeScript "RequirePartnerCenterIdentity"
    Assert-Contains "Store package script exposes an explicit acceptance-only mode" $storeScript "AcceptanceOnly"
    Assert-Contains "Store package script requires exactly one packaging mode" $storeScript "Assert-PackageMode"
    Assert-Contains "Store package script rejects combining acceptance-only and Partner Center identity modes" $storeScript '\$AcceptanceOnly\s+-eq\s+\$RequirePartnerCenterIdentity'
    Assert-Contains "Store package script enforces local identity for acceptance-only packages" $storeScript "Assert-AcceptanceOnlyIdentity"
    Assert-Contains "Store package script rejects the local identity at the production gate" $storeScript '\$sourceIdentity\.Name\s+-eq\s+''DuduDesktop\.Local\.NonProduction'''
    Assert-Contains "Store package script compares the expected production name" $storeScript '\$sourceIdentity\.Name\s+-ne\s+\$ExpectedPartnerCenterName'
    Assert-Contains "Store package script compares the expected production publisher" $storeScript '\$sourceIdentity\.Publisher\s+-ne\s+\$ExpectedPartnerCenterPublisher'
    $storeScriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $storeScriptPath,
        [ref]$null,
        [ref]$null
    )
    $identityFunction = @($storeScriptAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-PartnerCenterIdentity'
    }, $true))
    Assert-True "Store package script exposes an executable identity validation function" ($identityFunction.Count -eq 1)
    if ($identityFunction.Count -eq 1) {
        . ([scriptblock]::Create($identityFunction[0].Extent.Text))
        [xml]$localIdentityManifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src/Dudu.App/Package.appxmanifest')
        $localIdentity = $localIdentityManifest.Package.Identity
        $rejectedLocalIdentity = $false
        try {
            Assert-PartnerCenterIdentity -sourceIdentity $localIdentity -ExpectedPartnerCenterName 'Contoso.Dudu' -ExpectedPartnerCenterPublisher 'CN=Contoso Dudu'
        }
        catch {
            $rejectedLocalIdentity = $true
        }
        Assert-True "Store identity function rejects the local non-production identity" $rejectedLocalIdentity
    }
    $manifestVersionFunction = @($storeScriptAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Set-StoreManifestVersion'
    }, $true))
    Assert-True "Store package script exposes a manifest-version staging function" ($manifestVersionFunction.Count -eq 1)
    if ($manifestVersionFunction.Count -eq 1) {
        . ([scriptblock]::Create($manifestVersionFunction[0].Extent.Text))
        [xml]$versionManifest = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="DuduDesktop.Local.NonProduction" Publisher="CN=Local" Version="1.0.0.0" ProcessorArchitecture="x64" /></Package>'
        $stagedVersion = $true
        try { Set-StoreManifestVersion -Manifest $versionManifest -Version '1.0.1.0' } catch { $stagedVersion = $false }
        Assert-True "manifest-version staging updates the requested package version" ($stagedVersion -and $versionManifest.Package.Identity.Version -eq '1.0.1.0')
    }
    Assert-Contains "Store package stages the requested manifest version before publishing" $storeScript 'Set-StoreManifestVersion\s+-Manifest\s+\$sourceManifest\s+-Version\s+\$expectedPackageVersion'
    Assert-Contains "Store package restores the source manifest after packaging" $storeScript 'WriteAllText\(\$manifestPath\s*,\s*\$originalManifestText'
    Assert-Contains "Store package script records the release commit SHA" $storeScript "'commit\.txt'"
    $acceptanceIdentityFunction = @($storeScriptAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-AcceptanceOnlyIdentity'
    }, $true))
    Assert-True "Store package script exposes an executable acceptance-only identity validation function" ($acceptanceIdentityFunction.Count -eq 1)
    if ($acceptanceIdentityFunction.Count -eq 1) {
        . ([scriptblock]::Create($acceptanceIdentityFunction[0].Extent.Text))
        [xml]$acceptanceManifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src/Dudu.App/Package.appxmanifest')
        $acceptedManifestIdentity = $true
        try {
            Assert-AcceptanceOnlyIdentity -sourceIdentity $acceptanceManifest.Package.Identity
        }
        catch {
            $acceptedManifestIdentity = $false
        }
        Assert-True "acceptance-only identity guard accepts the real manifest identity" $acceptedManifestIdentity

        [xml]$nonLocalManifest = '<Package><Identity Name="DuduDesktop.Local.NonProduction" Publisher="CN=Not Dudu" /></Package>'
        $rejectedNonLocalIdentity = $false
        try {
            Assert-AcceptanceOnlyIdentity -sourceIdentity $nonLocalManifest.Package.Identity
        }
        catch {
            $rejectedNonLocalIdentity = $true
        }
        Assert-True "acceptance-only identity guard rejects a different publisher" $rejectedNonLocalIdentity
    }
    $packageModeFunction = @($storeScriptAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-PackageMode'
    }, $true))
    Assert-True "Store package script exposes an executable packaging mode validator" ($packageModeFunction.Count -eq 1)
    if ($packageModeFunction.Count -eq 1) {
        . ([scriptblock]::Create($packageModeFunction[0].Extent.Text))
        $modeCases = @(
            @{ Name = 'neither'; AcceptanceOnly = $false; RequirePartnerCenterIdentity = $false; Reject = $true },
            @{ Name = 'both'; AcceptanceOnly = $true; RequirePartnerCenterIdentity = $true; Reject = $true },
            @{ Name = 'acceptance-only'; AcceptanceOnly = $true; RequirePartnerCenterIdentity = $false; Reject = $false },
            @{ Name = 'Partner Center'; AcceptanceOnly = $false; RequirePartnerCenterIdentity = $true; Reject = $false }
        )
        foreach ($modeCase in $modeCases) {
            $rejectedMode = $false
            try {
                Assert-PackageMode -AcceptanceOnly:$modeCase.AcceptanceOnly -RequirePartnerCenterIdentity:$modeCase.RequirePartnerCenterIdentity
            }
            catch {
                $rejectedMode = $true
            }
            Assert-True "Store package mode validator handles $($modeCase.Name) correctly" ($rejectedMode -eq $modeCase.Reject)
        }
    }
    $strictOutputFunction = @($storeScriptAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-StrictStorePackageOutput'
    }, $true))
    Assert-True "Store package script exposes an executable strict package-output validator" ($strictOutputFunction.Count -eq 1)
    if ($strictOutputFunction.Count -eq 1) {
        . ([scriptblock]::Create($strictOutputFunction[0].Extent.Text))
        $strictOutputCases = @(
            @{ Name = 'one root MSIX'; Files = @('DuduDesktop_1.0.0.0_x64.msix'); Reject = $false },
            @{ Name = 'no files'; Files = @(); Reject = $true },
            @{ Name = 'two MSIX files'; Files = @('DuduDesktop_1.0.0.0_x64.msix', 'stale.msix'); Reject = $true },
            @{ Name = 'MSIX plus extra file'; Files = @('DuduDesktop_1.0.0.0_x64.msix', 'publish.log'); Reject = $true },
            @{ Name = 'MSIX upload container'; Files = @('DuduDesktop.msixupload'); Reject = $true },
            @{ Name = 'AppX upload container'; Files = @('DuduDesktop.appxupload'); Reject = $true },
            @{ Name = 'App Installer'; Files = @('DuduDesktop.appinstaller'); Reject = $true },
            @{ Name = 'AppX package'; Files = @('DuduDesktop.appx'); Reject = $true },
            @{ Name = 'MSIX bundle'; Files = @('DuduDesktop.msixbundle'); Reject = $true },
            @{ Name = 'AppX bundle'; Files = @('DuduDesktop.appxbundle'); Reject = $true },
            @{ Name = 'nested MSIX'; Files = @('nested/DuduDesktop.msix'); Reject = $true }
        )
        foreach ($strictOutputCase in $strictOutputCases) {
            $rejectedOutput = $false
            try {
                Assert-StrictStorePackageOutput -RelativeFilePaths $strictOutputCase.Files
            }
            catch {
                $rejectedOutput = $true
            }
            Assert-True "strict package-output validator handles $($strictOutputCase.Name) correctly" ($rejectedOutput -eq $strictOutputCase.Reject)
        }
    }
    $appCertReportFunction = @($storeScriptAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-AppCertReportStoreReady'
    }, $true))
    Assert-True "Store package script exposes an executable WACK Store-readiness validator" ($appCertReportFunction.Count -eq 1)
    if ($appCertReportFunction.Count -eq 1) {
        . ([scriptblock]::Create($appCertReportFunction[0].Extent.Text))
        $reportTestDirectory = Join-Path ([IO.Path]::GetTempPath()) ("dudu-appcert-contract-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $reportTestDirectory -Force | Out-Null
        try {
            $passingReport = Join-Path $reportTestDirectory 'passing.xml'
            Set-Content -LiteralPath $passingReport -NoNewline -Encoding utf8 -Value '<REPORT OVERALL_RESULT="PASS" VERSION="10.0.26100.0" />'
            $acceptedPassingReport = $true
            try { Assert-AppCertReportStoreReady -ReportPath $passingReport } catch { $acceptedPassingReport = $false }
            Assert-True "WACK Store-readiness validator accepts the schema-level PASS result" $acceptedPassingReport

            $optionalWarningReport = Join-Path $reportTestDirectory 'optional-warning.xml'
            Set-Content -LiteralPath $optionalWarningReport -NoNewline -Encoding utf8 -Value '<REPORT OVERALL_RESULT="WARNING" VERSION="10.0.26100.0"><REQUIREMENTS><REQUIREMENT NUMBER="24"><TEST OPTIONAL="TRUE"><RESULT>FAIL</RESULT></TEST></REQUIREMENT><REQUIREMENT NUMBER="26"><TEST OPTIONAL="FALSE"><RESULT>WARNING</RESULT></TEST></REQUIREMENT></REQUIREMENTS></REPORT>'
            $acceptedOptionalWarningReport = $true
            try { Assert-AppCertReportStoreReady -ReportPath $optionalWarningReport } catch { $acceptedOptionalWarningReport = $false }
            Assert-True "WACK Store-readiness validator accepts optional failures and required warnings" $acceptedOptionalWarningReport

            $requiredFailureReport = Join-Path $reportTestDirectory 'required-failure.xml'
            Set-Content -LiteralPath $requiredFailureReport -NoNewline -Encoding utf8 -Value '<REPORT OVERALL_RESULT="WARNING" VERSION="10.0.26100.0"><REQUIREMENTS><REQUIREMENT NUMBER="24"><TEST OPTIONAL="FALSE"><RESULT>FAIL</RESULT></TEST></REQUIREMENT></REQUIREMENTS></REPORT>'
            $rejectedRequiredFailureReport = $false
            try { Assert-AppCertReportStoreReady -ReportPath $requiredFailureReport } catch { $rejectedRequiredFailureReport = $true }
            Assert-True "WACK Store-readiness validator rejects required-test failures" $rejectedRequiredFailureReport

            $failedReport = Join-Path $reportTestDirectory 'failed.xml'
            Set-Content -LiteralPath $failedReport -NoNewline -Encoding utf8 -Value '<REPORT OVERALL_RESULT="FAIL" VERSION="10.0.26100.0" />'
            $rejectedFailedReport = $false
            try { Assert-AppCertReportStoreReady -ReportPath $failedReport } catch { $rejectedFailedReport = $true }
            Assert-True "WACK Store-readiness validator rejects the schema-level FAIL result" $rejectedFailedReport

            $malformedReport = Join-Path $reportTestDirectory 'malformed.xml'
            Set-Content -LiteralPath $malformedReport -NoNewline -Encoding utf8 -Value '<RESULT OVERALL_RESULT="PASS" />'
            $rejectedMalformedReport = $false
            try { Assert-AppCertReportStoreReady -ReportPath $malformedReport } catch { $rejectedMalformedReport = $true }
            Assert-True "WACK Store-readiness validator rejects a non-WACK report schema" $rejectedMalformedReport

            $rejectedMissingReport = $false
            try { Assert-AppCertReportStoreReady -ReportPath (Join-Path $reportTestDirectory 'missing.xml') } catch { $rejectedMissingReport = $true }
            Assert-True "WACK Store-readiness validator rejects a missing report" $rejectedMissingReport
        }
        finally {
            Remove-Item -LiteralPath $reportTestDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    $appCertResetInvocations = @($storeScriptAst.FindAll({
        param($node)
        if ($node -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
        $elements = @($node.CommandElements | ForEach-Object { $_.Extent.Text })
        return $elements.Count -ge 2 -and $elements[0] -eq '$appCert' -and $elements[1] -eq 'reset'
    }, $true))
    $appCertTestInvocations = @($storeScriptAst.FindAll({
        param($node)
        if ($node -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
        $elements = @($node.CommandElements | ForEach-Object { $_.Extent.Text })
        return $elements.Count -ge 2 -and $elements[0] -eq '$appCert' -and $elements[1] -eq 'test'
    }, $true))
    Assert-True "production WACK resets AppCert before testing" (
        $appCertResetInvocations.Count -eq 1 -and
        $appCertTestInvocations.Count -eq 1 -and
        $appCertResetInvocations[0].Extent.StartOffset -lt $appCertTestInvocations[0].Extent.StartOffset
    )
    Assert-Contains "production WACK validates its report after appcert test" $storeScript 'Assert-AppCertReportStoreReady\s+-ReportPath\s+\$appCertReport'
    $clearOutputIndex = $storeScript.IndexOf("Remove-Item -LiteralPath `$directory -Recurse -Force")
    $preflightCommentIndex = $storeScript.IndexOf("# Validate every host, source, and tool input")
    $preflightTryIndex = $storeScript.IndexOf("try {", $preflightCommentIndex)
    $hostPreflightIndex = $storeScript.IndexOf("OperatingSystem]::IsWindows")
    $manifestPreflightIndex = $storeScript.IndexOf("`$originalManifestText = Get-Content -Raw -LiteralPath `$manifestPath")
    $sourceManifestIndex = $storeScript.IndexOf("[xml]`$sourceManifest = `$originalManifestText")
    $toolPreflightIndex = $storeScript.IndexOf("`$sdkTools = Resolve-WindowsSdkTools")
    Assert-True "Store host, manifest, and tool preflight use the protected evidence path before package output deletion" (
        $clearOutputIndex -ge 0 -and $preflightCommentIndex -ge 0 -and $preflightTryIndex -ge 0 -and $hostPreflightIndex -gt $preflightTryIndex -and
        $manifestPreflightIndex -ge 0 -and $sourceManifestIndex -gt $manifestPreflightIndex -and $toolPreflightIndex -ge 0 -and
        $manifestPreflightIndex -lt $clearOutputIndex -and $toolPreflightIndex -lt $clearOutputIndex
    )
    Assert-Contains "Store package script sanitizes protected preflight evidence" $storeScript 'token\|secret\|password'
    Assert-Contains "Store package script records post-cleanup failure evidence" $storeScript 'Write-PostCleanupFailureEvidence'
    Assert-Contains "Store package script marks failed package output invalid" $storeScript 'Submission artifact: invalid'
    Assert-Contains "Store package script records the failed stage in metadata" $storeScript 'Store package stage:'
    $appCertSummaryIndex = $storeScript.IndexOf("Windows App Certification Kit exit code")
    $appCertThrowIndex = $storeScript.IndexOf('Windows App Certification Kit validation failed')
    Assert-True "Store package script records WACK results before throwing" ($appCertSummaryIndex -ge 0 -and $appCertThrowIndex -ge 0 -and $appCertSummaryIndex -lt $appCertThrowIndex)
    Assert-Contains "Store package script records acceptance-only WACK omission" $storeScript "Windows App Certification Kit status: skipped"
    Assert-Contains "Store package script keeps WACK tooling mandatory outside acceptance-only mode" $storeScript 'Resolve-WindowsSdkTools\s+-RequireAppCert:\(-not \$AcceptanceOnly\)'
    $hashIndex = $storeScript.IndexOf("Get-FileHash -LiteralPath `$artifactPath")
    Assert-True "Store package script hashes only after WACK validation" ($appCertThrowIndex -ge 0 -and $hashIndex -gt $appCertThrowIndex)
}

Assert-Contains "Inno has a default AppVersion define" $iss '#ifndef AppVersion'
Assert-Contains "Inno receives AppVersion from the compiler define" $iss 'AppVersion=\{#AppVersion\}'
Assert-Contains "Inno output filename includes the compiler version" $iss 'OutputBaseFilename=DuduDesktop-\{#AppVersion\}-win-x64-private'
Assert-Contains "publish forwards Version to Inno" $publish '"/DAppVersion=\$Version"'
Assert-Contains "publish preflights the private asset pack" $publish 'Assert-PrivateReleaseAssetPack'
Assert-Contains "publish preflights the private audio pack" $publish 'Assert-PrivateAudioReleaseAssetPack'
Assert-Contains "publish allowlist permits only the private audio manifest" $publish 'Assets/Audio/private-dudu/manifest\\\.json'
Assert-Contains "publish allowlist permits only private WAV files" $publish 'Assets/Audio/private-dudu/.*\.wav'
Assert-Contains "audio manifest requires private use" $publish 'private audio release pack manifest must declare privateUseOnly: true'
Assert-Contains "audio manifest requires exact five-pack contract" $publish 'exactly the five required pack ids'
foreach ($audioPackId in @('bubu-dudu-atata', 'tata-lala', 'dudu-lalala', 'dudu-atatata', 'dudu-yapapa')) {
    Assert-Contains "publish names required audio pack $audioPackId" $publish $audioPackId
}
Assert-Contains "audio validator rejects unreferenced WAVs" $publish 'unreferenced WAV'
Assert-Contains "audio validator rejects unsafe cue paths" $publish 'unsafe or non-WAV cue path'
Assert-Contains "publish checks the exact file manifest before ISCC" $publish 'Assert-PublishManifest'
Assert-Contains "publish moves generated package locks out of the source tree" $publish 'Move-GeneratedPackageLocksToScratch'
Assert-Contains "verify generates a per-run e2e secret" $verify '\[Guid\]::NewGuid'
Assert-True "verify carries no static e2e secret" ($verify -notmatch 'verification-secret-1042')
Assert-Contains "e2e scrubs secret-bearing temp files" $e2e 'Clear-SensitiveFile'
Assert-True "workflow pins every action to a commit SHA" (($workflow | Select-String 'uses:\s+\S+@v\d+\s*$' -AllMatches).Matches.Count -eq 0)
Assert-Contains "workflow attests build provenance" $workflow 'attest-build-provenance'
Assert-Contains "workflow publishes the hash to the job summary" $workflow 'GITHUB_STEP_SUMMARY'
Assert-True "workflow pins every action to a full 40-char SHA" (@([regex]::Matches($workflow, '(?m)^\s*-?\s*uses:\s*(\S+)') | Where-Object { $_.Groups[1].Value -notmatch '@[0-9a-f]{40}$' }).Count -eq 0)
Assert-Contains "workflow serializes runs with a concurrency group" $workflow '(?m)^concurrency:'
Assert-Contains "workflow has minimal top-level permissions" $workflow '(?m)^permissions:\s*\r?\n\s+contents:\s*read\s*$'
Assert-Contains "workflow re-verifies the stored artifact hash in a separate job" $workflow 'actions/download-artifact@'
Assert-Contains "workflow uploads release metadata" $workflow 'artifacts/release-metadata/'
Assert-Contains "workflow invokes the Store package wrapper" $workflow "scripts/package-store\.ps1"
Assert-Contains "workflow invokes the Store wrapper in explicit acceptance-only mode" $workflow 'scripts/package-store\.ps1\s+-Version \$env:STORE_VERSION\s+-AcceptanceOnly'
Assert-Contains "workflow uploads a Store package artifact" $workflow 'DuduDesktop-\$\{\{ env\.STORE_VERSION \}\}-win-x64-store'
Assert-Contains "workflow supports an explicit Store package version" $workflow "store_version"
Assert-Contains "workflow requires a Store version for manual dispatch" $workflow "store_version:(?s).*required:\s*true"
Assert-Contains "workflow keeps 1.0.0 as the push Store acceptance version" $workflow "github\.event_name\s*==\s*'push'.*1\.0\.0"
Assert-Contains "Store package restore downloads the win-x64 runtime pack" $workflow '(?is)store-package:.*?dotnet restore DuduDesktop\.slnx -r win-x64 -p:RestorePackagesWithLockFile=false'
Assert-True "workflow disables source lock-file generation during non-RID restores" (@([regex]::Matches($workflow, 'dotnet restore DuduDesktop\.slnx -p:RestorePackagesWithLockFile=false')).Count -eq 2)
Assert-Contains "workflow writes the Store package hash to the job summary" $workflow "Store package SHA-256"
Assert-Contains "workflow writes the hosted WACK omission to the Store job summary" $workflow "WACK status: skipped"
Assert-Contains "workflow labels the Store artifact acceptance-only" $workflow "acceptance-only"
Assert-Contains "workflow forbids Store artifact upload to Partner Center" $workflow "never upload it to Partner Center"
Assert-Contains "workflow uploads Store validation evidence after package failure" $workflow 'if:\s*\$\{\{ always\(\) && steps\.package_store\.outcome == ''failure'' \}\}'
Assert-Contains "workflow uses a separate Store failure-evidence artifact" $workflow 'DuduDesktop-\$\{\{ env\.STORE_VERSION \}\}-win-x64-store-failure-evidence'
Assert-Contains "workflow retains Store validation summary evidence" $workflow 'artifacts/store-package-metadata/validation-summary\.txt'
Assert-Contains "workflow retains Store AppCert evidence when present" $workflow 'artifacts/store-package-metadata/appcert-report\.xml'
Assert-Contains "workflow retains Store preflight failure evidence" $workflow 'work/store-package-failures/\*\*/validation-summary\.txt'
Assert-Contains "workflow writes safe Store failure metadata when packaging exits early" $workflow 'artifacts/store-package-failure-metadata/failure-metadata\.txt'
Assert-Contains "verify runs Store package source contracts" $verify 'pwsh tests/scripts/store-package\.tests\.ps1'
Assert-Contains "verify explicitly selects the non-public acceptance-only Store package mode" $verify 'scripts/package-store\.ps1\s+-Version 1\.0\.0\s+-AcceptanceOnly'
Assert-Contains "workflow runs Store package source contracts" $workflow 'pwsh tests/scripts/store-package\.tests\.ps1'
Assert-Contains "workflow keeps the Inno artifact" $workflow "DuduDesktop-1\.0\.0-win-x64-private"
Assert-True "Store package job does not expose secrets in logs" ($workflow -notmatch "echo.*\bSTORE\b|Write-Host.*\bSTORE\b.*\bSECRET\b")
Assert-True "production workflow pins every action to a full 40-char SHA" (@([regex]::Matches($productionWorkflow, '(?m)^\s*-?\s*uses:\s*(\S+)') | Where-Object { $_.Groups[1].Value -notmatch '@[0-9a-f]{40}$' }).Count -eq 0)
Assert-Contains "production workflow requires the test job before packaging" $productionWorkflow '(?s)production-store-package:.*?needs:\s*tests'
$globalJson = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "global.json") | ConvertFrom-Json
Assert-True "global.json pins the SDK exactly (rollForward disable)" ($globalJson.sdk.rollForward -eq "disable")
$packagesProps = [xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot "Directory.Packages.props"))
Assert-True "Directory.Packages.props has no floating/range versions" (@($packagesProps.Project.ItemGroup.PackageVersion | Where-Object { $_.Version -notmatch '^\d+(\.\d+){1,3}$' }).Count -eq 0)
Assert-Contains "publish records lock files and dotnet --info" $publish 'Write-ReleaseMetadata'
Assert-Contains "publish records the release commit SHA" $publish 'commit\.txt'
Assert-Contains "smoke test uses the app data-root override" $smoke 'DUDU_DATA_ROOT'
Assert-Contains "smoke test uses an isolated installer directory" $smoke '"dudu-installer-smoke-\$runId"'
Assert-Contains "smoke test creates an outside sentinel" $smoke 'dudu-installer-smoke-sentinel-\$runId'
Assert-Contains "smoke test can exercise a running app during upgrade" $smoke 'ExerciseRunningApp'
Assert-Contains "smoke test can exercise a normal interactive launch" $smoke 'ExerciseNormalLaunch'
Assert-Contains "normal launch checks startup failure diagnostics" $smoke 'startup-failure\.log'
Assert-Contains "normal launch closes the exact process cleanly" $smoke 'Close-NormalLaunchApp'
Assert-True "installer contains no image-wide Dudu taskkill" ($iss -notmatch 'taskkill\s+/IM\s+Dudu\.App\.exe')
Assert-Contains "Inno download verifies before Start-Process" $innoScript 'Assert-PinnedInnoSetupFile -Path \$downloadPath'
Assert-Contains "app copies the private animation pack" $appProject '<Content Include="Assets/Packs/private-dudu/\*\*" CopyToOutputDirectory="PreserveNewest" />'
Assert-Contains "app copies the private audio pack" $appProject '<Content Include="Assets/Audio/private-dudu/\*\*" CopyToOutputDirectory="PreserveNewest" />'
Assert-True "app does not copy audio source downloads" ($appProject -notmatch 'assets[/\\]sources|work[/\\]audio-source')
Assert-Contains "verify inspects the private audio manifest" $verify 'Assets/Audio/private-dudu/manifest\.json'
Assert-Contains "smoke test checks the installed audio manifest" $smoke 'Assets\\Audio\\private-dudu\\manifest\.json'
Assert-Contains "smoke test checks every referenced audio WAV" $smoke '(?s)privateAudioManifest\.packs.*cues'
Assert-Contains "release docs keep audio private" $releaseDoc '(?is)audio.*private-use-only'
Assert-Contains "release docs prohibit runtime audio downloads" $releaseDoc '(?is)audio.*not\s+runtime-downloaded'
Assert-Contains "release docs require separate audio redistribution rights" $releaseDoc '(?is)audio.*separate redistribution rights'

$manifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/Dudu.App/Assets/Packs/private-dudu/manifest.json") | ConvertFrom-Json
Assert-True "private release manifest declares privateUseOnly" ($manifest.privateUseOnly -eq $true)
$privatePackRoot = Join-Path $repoRoot "src/Dudu.App/Assets/Packs/private-dudu"
Assert-True "private release pack contains asset files" (@(Get-ChildItem -LiteralPath $privatePackRoot -Recurse -File).Count -gt 1)
$idleFrame = $manifest.outfits.base.animations.idle.frames[0].file
$blinkFrame = $manifest.outfits.base.animations.blink.frames[0].file
Assert-True "private idle frame is a clean character frame" ($idleFrame -notmatch '536e8919d09d')
Assert-True "private blink frame is a clean character frame" ($blinkFrame -notmatch '536e8919d09d')
foreach ($animationName in @('idle', 'blink', 'greeting', 'sleep', 'drink', 'focus', 'celebrate', 'comfort-hug', 'note-arrival')) {
    $animation = $manifest.outfits.base.animations.$animationName
    Assert-True "private $animationName has a referenced first frame" ($null -ne $animation -and $animation.frames.Count -gt 0 -and (Test-Path -LiteralPath (Join-Path $privatePackRoot $animation.frames[0].file)))
}

Write-Host ""
if ($script:FailureCount -gt 0) {
    $failureCount = $script:FailureCount
    $caseCount = $script:CaseCount
    Write-Host ("release-contract.tests.ps1: FAIL " + $failureCount + " of " + $caseCount + " cases failed")
    exit 1
}
$caseCount = $script:CaseCount
Write-Host ("release-contract.tests.ps1: PASS " + $caseCount + " cases")
exit 0
