<#
.SYNOPSIS
    Resets Quiesce's own bookkeeping to a fresh install WITHOUT touching the machine and WITHOUT
    losing the entries you authored yourself.

.DESCRIPTION
    Run it through the launcher:  .\q.cmd clean-slate

    "Delete %ProgramData%\Quiesce" is the obvious way to start over and it is the wrong one, twice:

      1. It takes user-apps.json with it - every app group and sign-in entry you added through the
         UI. On this machine that is fifteen entries which would have to be re-authored by hand.

      2. Done while the machine is ENGAGED it strands every applied change with no undo. The journal
         is the only record of what the prior values were. Deleting it does not un-apply anything; it
         just makes the changes permanent and anonymous. That is the exact failure this whole product
         is organised against, so this script REFUSES rather than warns.

    So it is surgical. Removed:

      state.json    the dirty flag, the active session, the reboot marker
      profiles.json which entries are switched on - so the next launch picks up the shipped default
      journal\      the sessions, their records and their generated revert.cmd scripts

    Kept:

      user-apps.json  the entries you authored
      settings.json   close-to-tray, start-with-Windows

    Everything removed is copied to a timestamped backup folder beside the data root first, so this
    is undoable by hand even though nothing here needs it to be.

.NOTES
    MUST be run ELEVATED. The data root is hardened to Administrators, so an unelevated probe of it
    returns a misleading false rather than an error.

    PowerShell 5.1 only on this machine; pwsh is not installed.
#>
[CmdletBinding()]
param(
    # Report what would happen and change nothing.
    [switch] $WhatIfOnly,

    # Proceed even though the machine is engaged. See the refusal text for what this costs you;
    # it is not offered in the launcher's help on purpose.
    [switch] $IAcceptStrandingAppliedChanges
)

$ErrorActionPreference = 'Stop'

$dataRoot = if ($env:QUIESCE_DATA_ROOT) { $env:QUIESCE_DATA_ROOT } else { 'C:\ProgramData\Quiesce' }

function Head($t) { Write-Host ''; Write-Host $t -ForegroundColor Cyan }
function Note($t) { Write-Host $t -ForegroundColor DarkGray }
function Bad ($t) { Write-Host $t -ForegroundColor Red }
function Warn($t) { Write-Host $t -ForegroundColor Yellow }

Head "Quiesce data root: $dataRoot"

if (-not (Test-Path $dataRoot)) {
    Write-Host '  absent - there is already nothing to reset.'
    exit 0
}

# Opened, not probed. Test-Path and File.Exists both answer "false" for "not permitted to look",
# and this directory is Administrators-only by design - so a probe run unelevated would report a
# clean machine and this script would cheerfully wipe an engaged one.
$statePath = Join-Path $dataRoot 'state.json'
$state = $null
try {
    $raw = Get-Content $statePath -Raw -ErrorAction Stop
    $state = $raw | ConvertFrom-Json
}
catch [System.UnauthorizedAccessException] {
    Bad '  cannot read state.json: access denied.'
    Bad '  Run this ELEVATED. Unelevated, this script cannot tell an engaged machine from a clean one.'
    exit 4
}
catch [System.Management.Automation.ItemNotFoundException] {
    Note '  no state.json - never engaged, or already reset.'
}

if ($null -ne $state) {
    $dirty = [bool]$state.isDirty
    Write-Host ("  state: {0}" -f $(if ($dirty) { "ENGAGED (session $($state.activeSessionId))" } else { 'clean' }))

    if ($dirty -and -not $IAcceptStrandingAppliedChanges) {
        Bad ''
        Bad '  REFUSED: this machine is engaged.'
        Bad ''
        Bad '  The journal is the only record of what your settings were before Engage. Deleting it'
        Bad '  does not put anything back - it makes every applied change permanent and removes the'
        Bad '  only thing that knew the prior values. Restore first:'
        Bad ''
        Bad '      .\q.cmd restore'
        Bad ''
        Bad '  then reboot if it asks, then run this again.'
        exit 5
    }

    if ($dirty) {
        Warn ''
        Warn '  PROCEEDING ON AN ENGAGED MACHINE because you passed the override.'
        Warn '  Whatever this session applied stays applied, and nothing will know how to undo it.'
    }
}

$removeFiles = @('state.json', 'profiles.json')
$removeDirs  = @('journal')
$keepFiles   = @('user-apps.json', 'settings.json')

Head 'Will remove'
foreach ($f in $removeFiles) {
    $p = Join-Path $dataRoot $f
    if (Test-Path $p) { Write-Host ("  {0,-16} {1,10:N0} bytes" -f $f, (Get-Item $p).Length) }
    else { Note ("  {0,-16} absent" -f $f) }
}
foreach ($d in $removeDirs) {
    $p = Join-Path $dataRoot $d
    if (Test-Path $p) {
        $n = @(Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue)
        Write-Host ("  {0,-16} {1} file(s)" -f ($d + '\'), $n.Count)
    }
    else { Note ("  {0,-16} absent" -f ($d + '\')) }
}

Head 'Will keep'
foreach ($f in $keepFiles) {
    $p = Join-Path $dataRoot $f
    if (Test-Path $p) { Write-Host ("  {0,-16} {1,10:N0} bytes  <- your own entries and preferences" -f $f, (Get-Item $p).Length) }
    else { Note ("  {0,-16} absent" -f $f) }
}

# Anything unrecognised is kept and named. A file this script has never heard of is not a file it
# should decide about, and silently leaving it would make the report above a lie by omission.
$known = $removeFiles + $keepFiles
$other = Get-ChildItem $dataRoot -File -ErrorAction SilentlyContinue | Where-Object { $known -notcontains $_.Name }
if ($other) {
    Head 'Not recognised, so kept and left alone'
    $other | ForEach-Object { Write-Host ("  {0,-16} {1,10:N0} bytes" -f $_.Name, $_.Length) }
}

if ($WhatIfOnly) {
    Head 'WhatIfOnly - nothing was changed.'
    exit 0
}

$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path (Split-Path -Parent $dataRoot) "Quiesce-backup-$stamp"

Head "Backing up to $backup"
New-Item -ItemType Directory -Force $backup | Out-Null
foreach ($f in $removeFiles) {
    $p = Join-Path $dataRoot $f
    if (Test-Path $p) { Copy-Item $p $backup -Force; Write-Host "  copied $f" }
}
foreach ($d in $removeDirs) {
    $p = Join-Path $dataRoot $d
    if (Test-Path $p) { Copy-Item $p $backup -Recurse -Force; Write-Host "  copied $d\" }
}

Head 'Removing'
foreach ($f in $removeFiles) {
    $p = Join-Path $dataRoot $f
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host "  removed $f" }
}
foreach ($d in $removeDirs) {
    $p = Join-Path $dataRoot $d
    if (Test-Path $p) { Remove-Item $p -Recurse -Force; Write-Host "  removed $d\" }
}

Head 'Done'
Write-Host '  Quiesce now reads as never-engaged, with a fresh default profile.'
Write-Host '  Your authored entries survived; they are simply not switched on yet.'
Note  "  backup: $backup"
Note  '  the machine itself was not touched by this script - only Quiesce''s bookkeeping.'
exit 0
