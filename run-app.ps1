<#
.SYNOPSIS
    Superseded by q.cmd. Kept as a shim so anything that still calls this keeps working.

.DESCRIPTION
    This script used to own the build-stage-launch dance. It now forwards to scripts\q.ps1, which
    does the same thing and additionally stops the instance already running - which matters more
    than it used to, because since the notification-area icon landed the old build is usually
    hidden rather than visibly open, and a second instance silently defers to the first instead of
    replacing it. Two builds in a row could therefore leave you looking at the older one.

    Prefer:  .\q.cmd
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

& (Join-Path $PSScriptRoot 'scripts\q.ps1') -Verb app -Configuration $Configuration -NoBuild:$NoBuild
exit $LASTEXITCODE
