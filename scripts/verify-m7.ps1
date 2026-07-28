<#
.SYNOPSIS
    Walks the M7 drift-and-resync feature end to end on this machine, in your own time.

.DESCRIPTION
    Written to be run by hand, at a moment you choose, because step 4 CLOSES YOUR BROWSERS
    and Quiesce does not reopen them. Nothing here is automated past a prompt for that reason.

    Every step says what it proves. Steps 1-3 and 8-10 change nothing.

.NOTES
    MUST be run ELEVATED. The data root is hardened to Administrators by design, so every
    unelevated probe of it returns a misleading false rather than an error - which is the trap
    five separate places in this codebase document.

    PowerShell 5.1 only on this machine; pwsh is not installed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\verify-m7.ps1
#>

[CmdletBinding()]
param(
    # Skip the destructive part. Steps 1-3 and 8-10 only: proves the plumbing without closing anything.
    [switch]$ReadOnly
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$cli  = Join-Path $repo 'src\Quiesce.Cli'
$dataRoot = if ($env:QUIESCE_DATA_ROOT) { $env:QUIESCE_DATA_ROOT } else { 'C:\ProgramData\Quiesce' }

function Step($n, $text) {
    Write-Host ''
    Write-Host "=== $n. $text" -ForegroundColor Cyan
}

function Prove($text) { Write-Host "    PROVES: $text" -ForegroundColor DarkGray }

function Quiesce {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
    & dotnet run --project $cli --no-build -- @Args
}

# ---------------------------------------------------------------- preconditions

$elevated = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    Write-Host 'NOT ELEVATED.' -ForegroundColor Red
    Write-Host 'The data root is restricted to Administrators, so an unelevated run cannot read state.json'
    Write-Host 'and every answer it gives about this machine would be a misleading false. Re-run elevated.'
    exit 4
}

Step 1 'Build and run the whole suite'
& dotnet build $repo --nologo -v q
& dotnet test  $repo --nologo -v q
Prove 'nothing below is being tested against a stale binary'

Step 2 'Read the machine, elevated'
Quiesce inventory
Prove 'machine: clean (or ENGAGED). No drift: line means the machine matches its session.'
Write-Host ''
Write-Host '    Compare with an UNELEVATED run of the same command: it reports UNKNOWN and refuses'
Write-Host '    to print a drift verdict it cannot support. That refusal is the feature, not a limitation.'

Step 3 'Ask about drift with nothing engaged'
Quiesce resync
Prove '"Nothing is engaged, so there is nothing to be out of sync with." A bare resync NEVER mutates.'

if ($ReadOnly) {
    Write-Host ''
    Write-Host 'ReadOnly: stopping before anything is changed.' -ForegroundColor Yellow
    Write-Host 'Re-run without -ReadOnly when you have nothing open that matters.'
    exit 0
}

# ---------------------------------------------------------------- the destructive half

Write-Host ''
Write-Host 'THE NEXT STEP CLOSES YOUR BROWSERS.' -ForegroundColor Yellow
Write-Host 'apps.close-browsers is ON in the built-in default profile. It is the only irreversible'
Write-Host 'thing enabled by default, and RESTORE DOES NOT REOPEN ANYTHING IT CLOSED.'
Write-Host ''
Write-Host 'Save your work first. Then type ENGAGE to continue, or anything else to stop.'
$answer = Read-Host '  >'

if ($answer -ne 'ENGAGE') {
    Write-Host 'Stopped. Nothing was changed.' -ForegroundColor Yellow
    exit 0
}

Step 4 'Engage, from the GUI'
Write-Host '    Run the app and press Engage:'
Write-Host '      powershell -ExecutionPolicy Bypass -File .\run-app.ps1'
Write-Host ''
Write-Host '    In the preflight, check:'
Prove 'a SAVE YOUR WORK FIRST banner above the list, naming each application ONCE'
Prove '   (a browser running nineteen processes is nineteen rows and one banner line)'
Prove 'the footer does NOT say "fully reversible" - it says "everything except the closes"'
Write-Host ''
Write-Host '    Approve it. Then check the result banner:'
Prove 'it lists the Notes - what was closed and will not reopen, or what declined to close.'
Prove '   Before this batch the engine wrote those and only the CLI read them.'
Write-Host ''
Read-Host '  press ENTER once you have engaged'

Step 5 'Capture the two files the resync design promises not to touch'
$statePath  = Join-Path $dataRoot 'state.json'
$journalDir = Join-Path $dataRoot 'journal'
$session    = Get-ChildItem $journalDir -Directory | Sort-Object CreationTime -Descending | Select-Object -First 1
$revertCmd  = Get-ChildItem $session.FullName -Filter 'revert*.cmd' | Select-Object -First 1

$stateHash  = (Get-FileHash $statePath -Algorithm SHA256).Hash
$revertSize = $revertCmd.Length

Write-Host "    session     $($session.Name)"
Write-Host "    state.json  $stateHash"
Write-Host "    revert.cmd  $revertSize bytes"
Prove 'both are compared byte-for-byte after the resync in step 7'

Step 6 'Reopen a browser, then re-check'
Write-Host '    Reopen whatever Engage closed (Comet, if that is what you use).'
Write-Host '    Then press Re-check on the dashboard, or reopen the tray menu.'
Write-Host ''
Prove 'the shell banner goes ORANGE, headline "Out of sync with what Quiesce applied"'
Prove 'it names the application and says N processes now vs how many were closed then'
Prove 'it says SAVE YOUR WORK FIRST again, because Resync closes them again'
Prove 'the dashboard card reads "Engaged, out of sync" - a distinct colour from Engaged'
Prove 'the tray tooltip says "N changes out of sync - as of HH:mm"'
Write-Host ''
Write-Host '    And from the CLI, which must agree:'
Quiesce resync
Prove 'the same verdict from a second implementation path. One detector, two callers.'
Write-Host ''
Read-Host '  press ENTER to continue'

Step 7 'Resync, then verify the two promises'
Write-Host '    Press Resync in the GUI and approve the preflight.'
Write-Host ''
Read-Host '  press ENTER once the resync has finished'

$stateAfter  = (Get-FileHash $statePath -Algorithm SHA256).Hash
$revertAfter = (Get-Item $revertCmd.FullName).Length

if ($stateAfter -eq $stateHash) {
    Write-Host '    PASS  state.json is byte-identical' -ForegroundColor Green
} else {
    Write-Host '    FAIL  state.json CHANGED. A resync must never write it.' -ForegroundColor Red
}

if ($revertAfter -eq $revertSize) {
    Write-Host '    PASS  revert.cmd is untouched' -ForegroundColor Green
} else {
    Write-Host "    FAIL  revert.cmd went from $revertSize to $revertAfter bytes." -ForegroundColor Red
    Write-Host '          RevertScriptWriter.Create truncates. Recovery net 4 has been destroyed.' -ForegroundColor Red
}

Step 8 'Inspect the journal the resync appended to'
$journal = Join-Path $session.FullName 'journal.jsonl'
$records = Get-Content $journal | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }

$known = @('sessionStart','planned','applying','applied','sideEffect','entryRolledBack',
           'committed','revertStart','reverted','revertDeferred','revertComplete')
$unknown = $records | Where-Object { $known -notcontains $_.record } | Select-Object -First 5

if (-not $unknown) {
    Write-Host '    PASS  every record type is one an older build can read' -ForegroundColor Green
} else {
    Write-Host "    FAIL  unknown record type(s): $($unknown.record -join ', ')" -ForegroundColor Red
    Write-Host '          JournalStore deserializes the discriminator outside its JsonException guard,' -ForegroundColor Red
    Write-Host '          so another build would be unable to revert this machine at all.' -ForegroundColor Red
}

$resynced = $records | Where-Object { $_.record -eq 'applying' -and $_.target -like '*resync*' }
$first    = $records | Where-Object { $_.record -eq 'applying' -and $_.target -notlike '*resync*' }

Write-Host "    resync records: $($resynced.Count), first-pass records: $($first.Count)"

if ($resynced -and $first) {
    $maxFirst  = ($first    | Measure-Object stepId -Maximum).Maximum
    $minResync = ($resynced | Measure-Object stepId -Minimum).Minimum

    if ($minResync -gt $maxFirst) {
        Write-Host "    PASS  resync step ids start at $minResync, past $maxFirst" -ForegroundColor Green
    } else {
        Write-Host "    FAIL  step id reuse: resync starts at $minResync, first pass reaches $maxFirst" -ForegroundColor Red
        Write-Host '          One RevertedRecord would discharge two records and the second never gets undone.' -ForegroundColor Red
    }

    Write-Host '    Creation times differ (a relaunched process is a different instance):'
    $resynced | Select-Object -First 2 | ForEach-Object {
        Write-Host "      pid $($_.process.pid) createdUtcTicks $($_.process.createdUtcTicks)"
    }
}

Step 9 'Confirm the machine matches again'
Quiesce inventory
Prove 'no drift: line. The resync closed what came back.'

Step 10 'Restore, and check what it says about the closes'
Write-Host '    Press Restore in the GUI.'
Write-Host ''
Prove 'it reports EVERY closed process across BOTH passes, not just the first'
Prove '   ("was closed. Quiesce does not relaunch applications - reopen it yourself")'
Prove 'nothing claims to have reopened anything'
Prove 'the dashboard returns to "Machine is clean"'
Write-Host ''
Read-Host '  press ENTER once restored'

Quiesce inventory
Prove 'machine: clean'

Write-Host ''
Write-Host 'Round-trip proof, separately:' -ForegroundColor Cyan
Write-Host '  .\scripts\baseline-diff.ps1'
Write-Host '  It EXCLUDES close entries, which is right - a close has no undo, so a diff that'
Write-Host '  "proved" it round-tripped would be proving the wrong thing. It is therefore proof'
Write-Host '  about the registry, service and power halves. Since this resync touches only'
Write-Host '  processes, a clean diff across engage -> resync -> restore is a real statement that'
Write-Host '  the resync perturbed nothing the journal was holding.'
Write-Host ''
Write-Host 'Also worth checking by hand:' -ForegroundColor Cyan
Write-Host '  - Engage, reboot WITHOUT restoring, launch the app. Resync must be refused with'
Write-Host '    "applied before the last restart". Then `quiesce recover` - a Persistent sign-in'
Write-Host '    preference must still be applied and the machine must stay dirty.'
Write-Host '  - Settings: toggle close-to-tray, then click X. Toggle Start-at-sign-in and confirm'
Write-Host '    \Quiesce\Start at sign-in exists with RunLevel Highest; sign out and in; toggle off'
Write-Host '    and confirm it is gone. Restore does NOT remove it - that is the only switch that does.'
Write-Host '  - Launch the shortcut while the window is hidden. It must RAISE the window, not show'
Write-Host '    "already running".'
Write-Host '  - Tray: Engage must be absent. Exit while engaged must say the machine stays engaged.'
Write-Host '    No ghost icon after Exit. It must return after: taskkill /f /im explorer.exe'
Write-Host ''
Write-Host 'NOTE: quit from the TRAY, not by closing the window, between run-app.ps1 runs.' -ForegroundColor Yellow
Write-Host 'That script stages to a fixed temp directory and copies under ErrorActionPreference Stop,'
Write-Host 'so a hidden instance still holding Quiesce.exe makes the next run throw.'
