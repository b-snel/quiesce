<#
.SYNOPSIS
    M3 acceptance: engage, restore, and prove the machine is byte-identical. Repeat to catch drift.

.DESCRIPTION
    The round-trip check inside `quiesce verify-revert` only inspects targets the catalog names, so
    it cannot see collateral damage. This script snapshots the WHOLE of every registry subtree the
    catalog touches - every sibling value, every subkey - before and after, and diffs them.

    Repeats the cycle N times because some drift only accumulates: a restore that leaves one extra
    value behind looks clean once and obvious after five rounds.

    Also captures the live mouse acceleration curve via SPI_GETMOUSE, because the mouse entry's
    revert is a system-parameter replay that a registry diff cannot see.

    READ-ONLY except for what Quiesce itself does. The script never writes the registry directly.

.PARAMETER Rounds
    How many engage/restore cycles to run. The plan calls for 5.

.PARAMETER Quiesce
    Path to quiesce.exe. Defaults to the Release build output.

.PARAMETER Only
    Entry-id prefixes to restrict the run to, e.g. -Only svc. or -Only svc.print-spooler.
    Lets the first service run be staged one service at a time instead of stopping nine at once.

.PARAMETER Skip
    Entry-id prefixes to exclude, e.g. -Skip svc. to cover the registry rows on their own.
#>
[CmdletBinding()]
param(
    [int] $Rounds = 5,
    [string] $Quiesce,
    [string[]] $Only = @(),
    [string[]] $Skip = @(),
    [string] $FaultInject
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Quiesce) {
    $found = Get-ChildItem (Join-Path $repo 'src\Quiesce.Cli\bin\Release') -Recurse -Filter quiesce.exe -ErrorAction SilentlyContinue |
             Where-Object { Test-Path ([System.IO.Path]::ChangeExtension($_.FullName, '.dll')) } |
             Select-Object -First 1
    if (-not $found) { throw "quiesce.exe not found. Build first: dotnet build -c Release" }
    $Quiesce = $found.FullName
}

# ---------------------------------------------------------------- snapshotting

<#
Three facts per service, captured the same way the engine captures them and for the same reason:
start type, delayed-auto flag and run state move independently. `Start` and `DelayedAutostart` are
read straight off the registry so an absent DelayedAutostart stays distinguishable from a zero -
issuing ChangeServiceConfig2 unconditionally MATERIALIZES that value, and a snapshot that folded
absent into 0 would call that silent mutation a clean restore.

Run state is the one fact with no registry home, so it is read from the SCM.
#>
function Get-ServiceFacts {
    param([string[]] $Names)

    $facts = [ordered]@{}
    foreach ($name in ($Names | Sort-Object)) {
        $path = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
        if (-not (Test-Path $path)) { $facts[$name] = '<SERVICE ABSENT>'; continue }

        $key     = Get-Item $path
        $start   = $key.GetValue('Start', '<absent>')
        $delayed = $key.GetValue('DelayedAutostart', '<absent>')
        $state   = try { (Get-Service -Name $name -ErrorAction Stop).Status } catch { '<unqueryable>' }

        $facts[$name] = "start=$start delayed=$delayed state=$state"
    }

    return $facts
}

function Get-Snapshot {
    param([string[]] $Subtrees, [string[]] $Services = @())

    $lines = [System.Collections.Generic.List[string]]::new()

    # Run state is not in any watched subtree, so it rides along here to be covered by the same
    # drift comparison as everything else.
    $facts = Get-ServiceFacts -Names $Services
    foreach ($name in $facts.Keys) { $lines.Add("SERVICE $name :: $($facts[$name])") }

    foreach ($path in ($Subtrees | Sort-Object)) {
        if (-not (Test-Path $path)) {
            $lines.Add("$path :: <KEY ABSENT>")
            continue
        }

        # Recurse: a restore that leaves litter in a child key is exactly the drift being hunted.
        $keys = @($path) + @(Get-ChildItem $path -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.PSPath -replace '^Microsoft\.PowerShell\.Core\\Registry::', '' })

        foreach ($key in ($keys | Sort-Object -Unique)) {
            $normalized = $key -replace '^HKEY_CURRENT_USER', 'HKCU:' -replace '^HKEY_LOCAL_MACHINE', 'HKLM:'
            try {
                $item = Get-Item $normalized -ErrorAction Stop
            } catch {
                continue
            }

            try { $names = @($item.GetValueNames() | Sort-Object) } catch { continue }

            foreach ($name in $names) {
                # A key or value can disappear between the enumeration and the read - volatile keys
                # and per-boot state do this constantly. Record it as unreadable rather than
                # throwing: an unreadable value that becomes readable later is drift worth seeing,
                # but it must not abort a five-round run on its way past.
                try {
                    $kind = $item.GetValueKind($name)
                    $raw = $item.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
                } catch {
                    $lines.Add("$normalized :: $name :: <UNREADABLE: $($_.Exception.Message)>")
                    continue
                }

                $rendered = if ($raw -is [byte[]]) { [BitConverter]::ToString($raw) }
                            elseif ($raw -is [string[]]) { $raw -join '|' }
                            else { "$raw" }
                $lines.Add("$normalized :: $name :: $kind :: $rendered")
            }
        }
    }

    return $lines
}

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Spi {
  [DllImport("user32.dll", SetLastError=true)]
  public static extern bool SystemParametersInfo(uint a, uint b, [Out] int[] p, uint f);
}
"@

<#
Runs a quiesce verb and returns its combined output.

Windows PowerShell 5.1 wraps every native-command stderr line in an ErrorRecord, which under
`$ErrorActionPreference = 'Stop'` turns quiesce's harmless dev-mode ACL warning into a terminating
error. Dropping to 'Continue' for the duration of the call is the only reliable way to invoke a
native exe here and still judge it by its exit code rather than by whether it printed anything.
#>
function Invoke-Quiesce {
    param([Parameter(Mandatory)][string] $Verb, [string[]] $Arguments = @())

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        return & $Quiesce $Verb @Arguments 2>&1 | ForEach-Object { "$_" }
    } finally {
        $ErrorActionPreference = $previous
    }
}

function Get-MouseCurve {
    $values = New-Object int[] 3
    if (-not [Spi]::SystemParametersInfo(0x0003, 0, $values, 0)) { return 'SPI_GETMOUSE failed' }
    return ($values -join ',')
}

# ---------------------------------------------------------------------- run

$catalogPath = if ($env:QUIESCE_CATALOG) { $env:QUIESCE_CATALOG } else { Join-Path $repo 'catalog\tweaks.json' }
if (-not (Test-Path $catalogPath)) { throw "Catalog not found at $catalogPath" }

$catalog = Get-Content $catalogPath -Raw | ConvertFrom-Json
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

# Test every entry we are actually able to apply. Unelevated, the HKLM rows cannot be written at
# all, so including them would make the whole run refuse rather than testing the rows it can.
# What gets skipped is reported, never silently dropped - a diff that quietly covers less than it
# claims is worse than one that covers less and says so.
$testable = if ($elevated) { $catalog.entries } else { $catalog.entries | Where-Object { -not $_.requiresAdmin } }
$skipped  = if ($elevated) { @() } else { $catalog.entries | Where-Object { $_.requiresAdmin } }

# -Only / -Skip narrow the run for staging. Applied after the elevation filter so the SKIPPED
# report still means "needs elevation" and never silently absorbs a deliberate exclusion.
$matchesPrefix = { param($id, $prefixes) foreach ($p in $prefixes) { if ($id.StartsWith($p, 'OrdinalIgnoreCase')) { return $true } } return $false }
if ($Only.Count -gt 0) { $testable = @($testable | Where-Object { & $matchesPrefix $_.id $Only }) }
if ($Skip.Count -gt 0) { $testable = @($testable | Where-Object { -not (& $matchesPrefix $_.id $Skip) }) }
if (@($testable).Count -eq 0) { throw "No entries selected. -Only $($Only -join ',') / -Skip $($Skip -join ',') matched nothing." }

# Drive the run through a scratch data root so the real machine state is untouched, and enable
# exactly the entries under test.
$scratchRoot = Join-Path $env:TEMP "quiesce-baseline-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force $scratchRoot | Out-Null
@{
    schemaVersion = 1
    active        = 'default'
    profiles      = @{ default = @{ enabled = @($testable.id) } }
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $scratchRoot 'profiles.json') -Encoding UTF8

$env:QUIESCE_DATA_ROOT = $scratchRoot
$env:QUIESCE_CATALOG = $catalogPath

Write-Host "quiesce  : $Quiesce"
Write-Host "catalog  : $catalogPath (v$($catalog.catalogVersion), $($catalog.entries.Count) entries)"
Write-Host "elevated : $elevated"
Write-Host "testing  : $($testable.Count) entr$(if ($testable.Count -eq 1) {'y'} else {'ies'})"
Write-Host "data root: $scratchRoot"
if ($skipped.Count -gt 0) {
    Write-Host "SKIPPED (need elevation - re-run this script as Administrator to cover them):" -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "  $($_.id)" -ForegroundColor Yellow }
}
Write-Host "rounds   : $Rounds"
Write-Host ""

# Watch only the subtrees the tested entries touch.
#
# Ops are polymorphic on `kind`, and a service op carries no hive or subkey. Reading them as
# registry ops yielded the path "HKCU:\" - the entire user hive - which recursed a few hundred
# thousand volatile values and died on the first one that vanished mid-enumeration. Dispatch on
# kind, and treat an op that names neither a subkey nor a service as a catalog error rather than
# quietly watching nothing.
$subtrees = [System.Collections.Generic.HashSet[string]]::new()
$serviceNames = [System.Collections.Generic.HashSet[string]]::new()
foreach ($entry in $testable) {
    foreach ($op in $entry.ops) {
        switch ($op.kind) {
            'registry' {
                if (-not $op.subkey) { throw "$($entry.id): registry op has no subkey" }
                $prefix = if ($op.hive -eq 'HKLM') { 'HKLM:' } else { 'HKCU:' }
                [void]$subtrees.Add("$prefix\$($op.subkey)")
            }
            'service' {
                if (-not $op.service) { throw "$($entry.id): service op has no service name" }
                [void]$serviceNames.Add($op.service)
                # The service's own key holds Start and DelayedAutostart, so the byte-level diff
                # covers service configuration for free - including the DelayedAutostart
                # materialization that a three-fact comparison alone would miss.
                [void]$subtrees.Add("HKLM:\SYSTEM\CurrentControlSet\Services\$($op.service)")
            }
            default { throw "$($entry.id): unknown op kind '$($op.kind)'" }
        }
    }
}
Write-Host "Watching $($subtrees.Count) registry subtree(s), recursively:"
$subtrees | Sort-Object | ForEach-Object { Write-Host "  $_" }
if ($serviceNames.Count -gt 0) {
    Write-Host "Watching $($serviceNames.Count) service(s) for start type, delayed-auto and run state:"
    $serviceNames | Sort-Object | ForEach-Object { Write-Host "  $_" }
}
Write-Host ""

$baseline = Get-Snapshot -Subtrees $subtrees -Services $serviceNames
$baselineMouse = Get-MouseCurve
$baselineSvc = Get-ServiceFacts -Names $serviceNames
Write-Host "Baseline: $($baseline.Count) values, mouse curve [$baselineMouse]"
foreach ($name in $baselineSvc.Keys) { Write-Host "  $name :: $($baselineSvc[$name])" }
Write-Host ""

$failures = 0

for ($round = 1; $round -le $Rounds; $round++) {
    Write-Host "--- round $round/$Rounds ---"

    # -FaultInject exists to test this harness's own failure path: a run that dies halfway must keep
    # its journal and revert.cmd, because that is the run that needs them.
    $engageOut = Invoke-Quiesce 'engage' $(if ($FaultInject) { @("--fault-inject=$FaultInject") } else { @() })
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ENGAGE FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        $engageOut | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        $failures++
        break
    }

    $engaged = Get-Snapshot -Subtrees $subtrees -Services $serviceNames
    $changed = (Compare-Object $baseline $engaged).Count
    Write-Host "  engaged: $changed value(s) differ from baseline, mouse curve [$(Get-MouseCurve)]"

    if ($changed -eq 0) {
        Write-Host "  WARNING: engage changed nothing - the catalog may already be applied" -ForegroundColor Yellow
    }

    # A refused service is reported by quiesce and then vanishes into an aggregate count: 24
    # registry entries applying is more than enough to keep `$changed` healthy while all nine
    # service steps quietly did nothing. A restore-clean run over a no-op is not evidence of
    # anything, so name every service that did not move and echo the refusal that explains it.
    $engagedSvc = Get-ServiceFacts -Names $serviceNames
    $inert = @($engagedSvc.Keys | Where-Object { $engagedSvc[$_] -eq $baselineSvc[$_] })
    foreach ($name in $engagedSvc.Keys) {
        if ($engagedSvc[$name] -ne $baselineSvc[$name]) {
            Write-Host "    $name : $($baselineSvc[$name])  ->  $($engagedSvc[$name])" -ForegroundColor Cyan
        }
    }
    if ($inert.Count -gt 0) {
        Write-Host "  NOT APPLIED - $($inert.Count) service(s) unchanged by engage:" -ForegroundColor Yellow
        foreach ($name in $inert) { Write-Host "    $name :: $($baselineSvc[$name])" -ForegroundColor Yellow }
        $engageOut | Where-Object { $_ -match 'refus|skip|guard|unavailable|StopRefused' } |
            ForEach-Object { Write-Host "    | $_" -ForegroundColor Yellow }
        $failures++
    }

    $restoreOut = Invoke-Quiesce 'restore'
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  RESTORE FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        $restoreOut | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        $failures++
        break
    }

    $restored = Get-Snapshot -Subtrees $subtrees -Services $serviceNames
    $drift = Compare-Object $baseline $restored
    $mouseNow = Get-MouseCurve

    if ($drift) {
        Write-Host "  DRIFT: $($drift.Count) difference(s) after restore" -ForegroundColor Red
        $drift | ForEach-Object { Write-Host "    $($_.SideIndicator) $($_.InputObject)" -ForegroundColor Red }
        $failures++
    } elseif ($mouseNow -ne $baselineMouse) {
        # Registry-clean but behaviour-dirty: the exact failure the activation replay exists to stop.
        Write-Host "  DRIFT: registry clean but mouse curve is [$mouseNow], was [$baselineMouse]" -ForegroundColor Red
        $failures++
    } else {
        Write-Host "  clean: byte-identical, mouse curve [$mouseNow]" -ForegroundColor Green
    }
}

Write-Host ""

# The scratch root holds the journal and the generated revert.cmd for this run. Deleting it
# unconditionally - as this script used to - destroys the only means of undoing a run that failed
# halfway, which is exactly the run that needs undoing. Clean up only on success.
if ($failures -eq 0) {
    Remove-Item $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "Journal and revert.cmd KEPT (a failed run may have left the machine dirty):" -ForegroundColor Yellow
    Write-Host "  $scratchRoot" -ForegroundColor Yellow
    Get-ChildItem $scratchRoot -Recurse -Filter revert.cmd -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "  undo with: `"$($_.FullName)`"" -ForegroundColor Yellow }
    Write-Host "  or: `$env:QUIESCE_DATA_ROOT='$scratchRoot'; & `"$Quiesce`" revert-all" -ForegroundColor Yellow
}

if ($failures -eq 0) {
    Write-Host "PASS - $Rounds round(s), no drift across $($testable.Count) entr$(if ($testable.Count -eq 1) {'y'} else {'ies'})." -ForegroundColor Green
    if ($skipped.Count -gt 0) {
        Write-Host "      ($($skipped.Count) admin-only entr$(if ($skipped.Count -eq 1) {'y'} else {'ies'}) not covered - re-run elevated.)" -ForegroundColor Yellow
    }
    exit 0
}

Write-Host "FAIL - $failures round(s) showed drift or a step that never applied." -ForegroundColor Red
exit 1
