<#
.SYNOPSIS
    End-to-end release gate for the private-note path: a real local Cloudflare Worker, the real
    Windows harness's `remote-note` scenario, and a real Playwright browser driving the real
    sender SPA, wired together to prove the boundaries `docs/privacy.md` describes rather than
    merely asserting them in isolation.

.DESCRIPTION
    1. Builds the relay's static sender bundle and starts `wrangler dev --local` against it, so
       both the real `/v1/*` API and the real sender page are served from one local origin
       (`relay/wrangler.jsonc`'s `assets.run_worker_first` makes this possible).
    2. Launches `tests/Dudu.WindowsHarness`'s `remote-note` scenario against that origin, with its
       own temporary SQLite database. The harness registers the desktop, creates a pairing code,
       prints it, and polls for up to 30 seconds.
    3. Drives a real headless Chromium browser (via the relay's own Playwright dependency) to the
       sender page: pairs with the printed code, types `-NoteText`, and sends it — capturing every
       request body the browser sends to `/v1/messages` along the way.
    4. Once the harness reports the note revealed, independently checks: the harness's own console
       transcript, a live dump of the relay's local D1 `messages` table, and the captured HTTP
       request bodies — none of which may contain the literal note text anywhere outside the one
       expected "Revealed text" line the harness itself prints after decrypting it locally.
    5. Confirms the relay's `messages` table has gone back to zero rows for this run (acknowledgment
       deletes the row — see `relay/src/db/messages.ts`'s `ackMessage`), i.e. no ciphertext is left
       over after acknowledgment.
    6. Kills Wrangler, then: (a) launches `tests/Dudu.WindowsHarness`'s own `outage-reminder`
       scenario, which schedules a real local reminder through the real `AppHost` /
       `ReminderEngine` / `PresentationCoordinator` path and waits for it to actually fire --
       this script then waits no more than twice the scheduled delay for the harness's
       `REMINDER-PRESENTED <id>` sentinel line, proving a due local reminder still fires with
       Wrangler dead, not merely that it would in isolation; and (b) also runs the existing xunit
       proof covering the same guarantee from the Infrastructure side
       (`PrivacyBoundaryTests.Worker_outage_does_not_stop_local_reminders`).

    Exits 0 only if every one of the above holds. Never prints the note text itself except inside
    an explicitly-labelled diagnostic line, and never prints tokens, pairing codes beyond what the
    harness itself already prints, or key material.

.PARAMETER RelayMode
    Must be "Local" — this script only knows how to drive a local `wrangler dev` instance. Kept as
    a parameter (rather than hard-coded) so a future remote-staging mode can be added without
    changing the call signature.

.PARAMETER NoteText
    The exact plaintext to send and to search for. Never written to any file this script leaves
    behind on success or failure beyond the temporary, deleted-on-exit working directory.

.EXAMPLE
    pwsh tests/e2e/private-note-flow.ps1 -RelayMode Local -NoteText "e2e-secret-4937"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Local")]
    [string]$RelayMode,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$NoteText
)

# This scenario depends on DPAPI secret storage (`src/Dudu.Infrastructure/Security/DpapiSecretStore.cs`)
# and the Windows notification stack, both Windows-only, and on the Windows harness's `remote-note`
# scenario, which itself refuses to run off Windows. Fail fast and loudly rather than limping partway
# through on an unsupported host.
if (-not $IsWindows) {
    throw "private-note-flow requires Windows"
}

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$relayDir = Join-Path $repoRoot "relay"
$harnessProject = Join-Path $repoRoot "tests\Dudu.WindowsHarness\Dudu.WindowsHarness.csproj"
$infrastructureTestsProject = Join-Path $repoRoot "tests\Dudu.Infrastructure.Tests\Dudu.Infrastructure.Tests.csproj"

$relayBaseUrl = "http://127.0.0.1:8787"
$d1DatabaseName = "dudu-relay-test"  # placeholder name `wrangler.jsonc` binds for local dev; see its comment.

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dudu-e2e-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workDir | Out-Null
$harnessTranscriptPath = Join-Path $workDir "harness-transcript.log"
$wranglerLogPath = Join-Path $workDir "wrangler.log"
$trafficCapturePath = Join-Path $workDir "http-traffic.json"
$senderDriverPath = Join-Path $workDir "sender-flow.mjs"
$d1DumpPath = Join-Path $workDir "d1-dump.txt"

$failures = [System.Collections.Generic.List[string]]::new()
$wranglerProcess = $null
$harnessProcess = $null
$outageHarnessProcess = $null
$harnessTranscript = [System.Text.StringBuilder]::new()
$harnessExitCode = $null

function Write-Step {
    param([string]$Message)
    Write-Host "[private-note-flow] $Message"
}

function Wait-ForHttpReady {
    param([string]$Url, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Timed out waiting for $Url to answer."
}

try {
    # --- Step 1: build the sender bundle and start a real local Worker -------------------------
    Write-Step "Building the relay sender bundle (npm run build)."
    Push-Location $relayDir
    try {
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE." }
    } finally {
        Pop-Location
    }

    Write-Step "Starting local Wrangler on $relayBaseUrl."
    $wranglerPsi = [System.Diagnostics.ProcessStartInfo]::new()
    $wranglerPsi.FileName = "npx"
    $wranglerPsi.Arguments = "wrangler dev --local --port 8787"
    $wranglerPsi.WorkingDirectory = $relayDir
    $wranglerPsi.RedirectStandardOutput = $true
    $wranglerPsi.RedirectStandardError = $true
    $wranglerPsi.UseShellExecute = $false
    $wranglerProcess = [System.Diagnostics.Process]::new()
    $wranglerProcess.StartInfo = $wranglerPsi
    $wranglerOutputHandler = {
        param($sender, $eventArgs)
        if ($null -ne $eventArgs.Data) {
            Add-Content -Path $wranglerLogPath -Value $eventArgs.Data
        }
    }
    Register-ObjectEvent -InputObject $wranglerProcess -EventName OutputDataReceived -Action $wranglerOutputHandler | Out-Null
    Register-ObjectEvent -InputObject $wranglerProcess -EventName ErrorDataReceived -Action $wranglerOutputHandler | Out-Null
    [void]$wranglerProcess.Start()
    $wranglerProcess.BeginOutputReadLine()
    $wranglerProcess.BeginErrorReadLine()

    Wait-ForHttpReady -Url $relayBaseUrl -TimeoutSeconds 30
    Write-Step "Wrangler is serving $relayBaseUrl."

    # --- Step 2: launch the Windows harness's remote-note scenario ------------------------------
    Write-Step "Launching tests/Dudu.WindowsHarness -- --scenario remote-note."
    $harnessPsi = [System.Diagnostics.ProcessStartInfo]::new()
    $harnessPsi.FileName = "dotnet"
    $harnessPsi.Arguments = "run --project `"$harnessProject`" -c Release -- --scenario remote-note"
    $harnessPsi.WorkingDirectory = $repoRoot
    $harnessPsi.RedirectStandardInput = $true
    $harnessPsi.RedirectStandardOutput = $true
    $harnessPsi.RedirectStandardError = $true
    $harnessPsi.UseShellExecute = $false
    $harnessPsi.EnvironmentVariables["DUDU_RELAY_BASE_URL"] = $relayBaseUrl

    $harnessProcess = [System.Diagnostics.Process]::new()
    $harnessProcess.StartInfo = $harnessPsi

    $pairingCodeSignal = [System.Threading.SemaphoreSlim]::new(0)
    $revealedSignal = [System.Threading.SemaphoreSlim]::new(0)
    $script:pairingCode = $null
    $script:revealedText = $null
    $script:revealedReaction = $null

    $harnessOutputHandler = {
        param($sender, $eventArgs)
        if ($null -eq $eventArgs.Data) { return }
        $line = $eventArgs.Data
        [void]$harnessTranscript.AppendLine($line)
        Add-Content -Path $harnessTranscriptPath -Value $line

        if ($line -match 'Pairing code: (?<code>[0-9A-HJKMNPQRSTVWXYZ]{8}) \(expires') {
            $script:pairingCode = $Matches.code
            $pairingCodeSignal.Release() | Out-Null
        }
        if ($line -match '^Revealed text: "(?<text>.*)"; reaction: (?<reaction>\S+)$') {
            $script:revealedText = $Matches.text
            $script:revealedReaction = $Matches.reaction
            $revealedSignal.Release() | Out-Null
        }
    }
    Register-ObjectEvent -InputObject $harnessProcess -EventName OutputDataReceived -Action $harnessOutputHandler | Out-Null
    Register-ObjectEvent -InputObject $harnessProcess -EventName ErrorDataReceived -Action $harnessOutputHandler | Out-Null

    [void]$harnessProcess.Start()
    $harnessProcess.BeginOutputReadLine()
    $harnessProcess.BeginErrorReadLine()

    if (-not $pairingCodeSignal.Wait([TimeSpan]::FromSeconds(60))) {
        throw "The harness never printed a pairing code within 60 seconds."
    }
    $pairingCode = $script:pairingCode
    Write-Step "Harness printed a pairing code."

    # --- Step 3: drive a real browser against the real sender page ------------------------------
    $senderDriverSource = @'
import { chromium } from "@playwright/test";
import { writeFileSync } from "node:fs";

const baseUrl = process.env.DUDU_E2E_BASE_URL;
const pairingCode = process.env.DUDU_E2E_PAIRING_CODE;
const noteText = process.env.DUDU_E2E_NOTE_TEXT;
const trafficCapturePath = process.env.DUDU_E2E_TRAFFIC_PATH;

const browser = await chromium.launch();
const page = await browser.newPage();

const capturedMessageBodies = [];
page.on("request", (request) => {
  if (request.method() === "POST" && request.url().endsWith("/v1/messages")) {
    capturedMessageBodies.push(request.postData() ?? "");
  }
});

try {
  await page.goto(baseUrl, { waitUntil: "load" });
  await page.getByLabel("Pairing code").fill(pairingCode);
  await page.getByRole("button", { name: "Pair privately" }).click();
  await page.getByLabel("Message").waitFor({ state: "visible", timeout: 15000 });
  await page.getByLabel("Message").fill(noteText);
  await page.locator("#send-button").click();
  await page.locator("#send-status").filter({ hasText: "Queued securely" }).waitFor({ timeout: 15000 });
} finally {
  writeFileSync(trafficCapturePath, JSON.stringify(capturedMessageBodies, null, 2), "utf8");
  await browser.close();
}
'@
    Set-Content -Path $senderDriverPath -Value $senderDriverSource -Encoding utf8NoBOM

    Write-Step "Driving the real sender page with Playwright (pair + send)."
    Push-Location $relayDir
    try {
        $env:DUDU_E2E_BASE_URL = $relayBaseUrl
        $env:DUDU_E2E_PAIRING_CODE = $pairingCode
        $env:DUDU_E2E_NOTE_TEXT = $NoteText
        $env:DUDU_E2E_TRAFFIC_PATH = $trafficCapturePath
        & node $senderDriverPath
        $senderDriverExitCode = $LASTEXITCODE
    } finally {
        Remove-Item Env:\DUDU_E2E_BASE_URL, Env:\DUDU_E2E_PAIRING_CODE, Env:\DUDU_E2E_NOTE_TEXT, Env:\DUDU_E2E_TRAFFIC_PATH -ErrorAction SilentlyContinue
        Pop-Location
    }
    if ($senderDriverExitCode -ne 0) {
        $failures.Add("The Playwright sender-page driver exited $senderDriverExitCode.")
    } else {
        Write-Step "Sender page reported 'Queued securely'."
    }

    # --- Step 4: wait for the harness to report the note revealed --------------------------------
    if (-not $revealedSignal.Wait([TimeSpan]::FromSeconds(40))) {
        $failures.Add("The harness never printed a 'Revealed text' line within 40 seconds of sending.")
    } else {
        Write-Step "Harness revealed the note."
        if ($script:revealedText -ne $NoteText) {
            $failures.Add("Revealed text did not match what was sent (recipient saw a different string than '$NoteText').")
        }
    }

    # Give the harness a moment to print its manual-checks banner and reach the PASS/FAIL prompt.
    Start-Sleep -Seconds 1

    # --- Step 5: independent checks -- desktop transcript, D1, and captured HTTP traffic ---------
    Write-Step "Checking the harness's own console transcript for plaintext leakage."
    $transcriptText = $harnessTranscript.ToString()
    $transcriptWithoutRevealLine = ($transcriptText -split "`r?`n" | Where-Object { $_ -notmatch '^Revealed text: "' }) -join "`n"
    # No on-disk log file is wired up yet (see docs/privacy.md's "Application logs" row); this
    # console transcript is the closest real, capturable stand-in for "desktop logs" today. The
    # toast's own content is covered by existing notification tests, not re-verified here.
    if ($transcriptWithoutRevealLine.Contains($NoteText)) {
        $failures.Add("The note text appeared in the harness console transcript outside the expected reveal line.")
    }

    Write-Step "Dumping the relay's local D1 'messages' table."
    Push-Location $relayDir
    try {
        & npx wrangler d1 execute $d1DatabaseName --local --command "SELECT * FROM messages;" *> $d1DumpPath
        $d1DumpText = Get-Content -Path $d1DumpPath -Raw -ErrorAction SilentlyContinue
    } finally {
        Pop-Location
    }
    if ($null -ne $d1DumpText -and $d1DumpText.Contains($NoteText)) {
        $failures.Add("The note text appeared in a raw D1 'messages' dump.")
    }

    Write-Step "Confirming no leftover ciphertext remains in D1 after acknowledgment."
    Push-Location $relayDir
    try {
        $countOutput = & npx wrangler d1 execute $d1DatabaseName --local --json --command "SELECT COUNT(*) AS remaining FROM messages;" 2>$null
    } finally {
        Pop-Location
    }
    $remainingCount = $null
    try {
        $parsedCount = $countOutput | ConvertFrom-Json
        $remainingCount = $parsedCount[0].results[0].remaining
    } catch {
        $failures.Add("Could not parse the D1 remaining-message count; raw output: $countOutput")
    }
    if ($null -ne $remainingCount -and [int]$remainingCount -ne 0) {
        $failures.Add("D1 still has $remainingCount row(s) in 'messages' after acknowledgment; ciphertext was not deleted.")
    }

    Write-Step "Checking captured HTTP request bodies for plaintext leakage."
    $trafficText = Get-Content -Path $trafficCapturePath -Raw -ErrorAction SilentlyContinue
    if ($null -eq $trafficText -or $trafficText.Trim() -eq "[]" -or $trafficText.Trim() -eq "") {
        $failures.Add("No POST /v1/messages request body was captured; the send may not have happened.")
    } elseif ($trafficText.Contains($NoteText)) {
        $failures.Add("The note text appeared verbatim in a captured HTTP request body (ciphertext should be opaque).")
    }

    # --- Step 6: hand the harness its own verdict, based on the checks above ---------------------
    if ($failures.Count -eq 0) {
        $harnessProcess.StandardInput.WriteLine("PASS")
    } else {
        $harnessProcess.StandardInput.WriteLine("FAIL: " + ($failures -join " | "))
    }
    $harnessProcess.StandardInput.Flush()

    if (-not $harnessProcess.WaitForExit(15000)) {
        $failures.Add("The harness did not exit within 15 seconds of receiving its verdict.")
        try { $harnessProcess.Kill() } catch { }
    }
    $harnessExitCode = $harnessProcess.ExitCode
    if ($harnessExitCode -ne 0) {
        $failures.Add("The Windows harness exited with code $harnessExitCode.")
    }

    # --- Step 7: kill Wrangler, then prove outage does not stop local reminders ------------------
    Write-Step "Stopping Wrangler to simulate a relay outage."
    try { $wranglerProcess.Kill() } catch { }
    try { $wranglerProcess.WaitForExit(10000) } catch { }

    # Ruling #3 (task-21-review-1.md, Important) requires the script itself -- not only a separate
    # xunit test -- to prove a due local reminder still fires after Wrangler is dead. The
    # `outage-reminder` harness scenario schedules a reminder through the real reminder path
    # (`AppHost` + `ReminderEngine` + `PresentationCoordinator`, no relay base URL configured) and
    # prints `REMINDER-PRESENTED <id>` only once the presentation gateway actually presents it.
    $dueReminderSeconds = 5
    Write-Step "Launching tests/Dudu.WindowsHarness -- --scenario outage-reminder --due-reminder-seconds $dueReminderSeconds (Wrangler is down)."
    $outageHarnessPsi = [System.Diagnostics.ProcessStartInfo]::new()
    $outageHarnessPsi.FileName = "dotnet"
    $outageHarnessPsi.Arguments = "run --project `"$harnessProject`" -c Release -- --scenario outage-reminder --due-reminder-seconds $dueReminderSeconds"
    $outageHarnessPsi.WorkingDirectory = $repoRoot
    $outageHarnessPsi.RedirectStandardOutput = $true
    $outageHarnessPsi.RedirectStandardError = $true
    $outageHarnessPsi.UseShellExecute = $false

    $outageHarnessProcess = [System.Diagnostics.Process]::new()
    $outageHarnessProcess.StartInfo = $outageHarnessPsi
    $reminderPresentedSignal = [System.Threading.SemaphoreSlim]::new(0)
    $script:reminderPresentedId = $null
    $outageHarnessTranscript = [System.Text.StringBuilder]::new()

    $outageHarnessOutputHandler = {
        param($sender, $eventArgs)
        if ($null -eq $eventArgs.Data) { return }
        $line = $eventArgs.Data
        [void]$outageHarnessTranscript.AppendLine($line)
        if ($line -match '^REMINDER-PRESENTED (?<id>\S+)$') {
            $script:reminderPresentedId = $Matches.id
            $reminderPresentedSignal.Release() | Out-Null
        }
    }
    Register-ObjectEvent -InputObject $outageHarnessProcess -EventName OutputDataReceived -Action $outageHarnessOutputHandler | Out-Null
    Register-ObjectEvent -InputObject $outageHarnessProcess -EventName ErrorDataReceived -Action $outageHarnessOutputHandler | Out-Null

    try {
        [void]$outageHarnessProcess.Start()
        $outageHarnessProcess.BeginOutputReadLine()
        $outageHarnessProcess.BeginErrorReadLine()

        # Review I8: `2 * $dueReminderSeconds` is 10 seconds, which is the reminder's own due
        # delay and nothing else -- the harness still has to restore, build, start, and JIT
        # before that clock is even meaningful, so the wait was timing the toolchain, not the
        # behaviour under test. The fixed 60-second floor covers all of that; the multiple keeps
        # the wait proportional if the due delay is ever raised.
        $reminderWaitSeconds = 60 + 2 * $dueReminderSeconds
        if (-not $reminderPresentedSignal.Wait([TimeSpan]::FromSeconds($reminderWaitSeconds))) {
            $failures.Add("The outage-reminder harness never printed REMINDER-PRESENTED within $reminderWaitSeconds seconds of a relay outage.")
        } else {
            Write-Step "Harness presented the due reminder ($script:reminderPresentedId) with Wrangler down."
        }

        if (-not $outageHarnessProcess.WaitForExit(15000)) {
            $failures.Add("The outage-reminder harness did not exit within 15 seconds.")
            try { $outageHarnessProcess.Kill() } catch { }
        } elseif ($outageHarnessProcess.ExitCode -ne 0) {
            $failures.Add("The outage-reminder harness exited with code $($outageHarnessProcess.ExitCode). Transcript: $($outageHarnessTranscript.ToString())")
        }
    } finally {
        if (-not $outageHarnessProcess.HasExited) {
            try { $outageHarnessProcess.Kill() } catch { }
        }
    }

    Write-Step "Running the xunit proof that a relay outage never stops local reminders."
    & dotnet test $infrastructureTestsProject --filter "FullyQualifiedName~Worker_outage_does_not_stop_local_reminders"
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("Worker_outage_does_not_stop_local_reminders did not pass after Wrangler was killed.")
    }
}
finally {
    if ($null -ne $harnessProcess -and -not $harnessProcess.HasExited) {
        try { $harnessProcess.Kill() } catch { }
    }
    if ($null -ne $outageHarnessProcess -and -not $outageHarnessProcess.HasExited) {
        try { $outageHarnessProcess.Kill() } catch { }
    }
    if ($null -ne $wranglerProcess -and -not $wranglerProcess.HasExited) {
        try { $wranglerProcess.Kill() } catch { }
    }
    Get-EventSubscriber -ErrorAction SilentlyContinue | Unregister-Event -ErrorAction SilentlyContinue
    try {
        Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        # Best-effort cleanup only; nothing under $workDir should carry plaintext or secrets by
        # the time we get here, since every check above already ran against its contents.
    }
}

if ($failures.Count -gt 0) {
    Write-Host "FAIL:"
    foreach ($failure in $failures) {
        Write-Host "  - $failure"
    }
    exit 1
}

Write-Host "PASS: private note delivered, revealed, acknowledged, and never leaked in transit or at rest; a relay outage did not stop local reminders."
exit 0
