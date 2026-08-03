<#
.SYNOPSIS
    One entry point for building, launching and driving Quiesce on this machine.

.DESCRIPTION
    Call it through q.cmd at the repo root. That wrapper exists so launching never needs
    -ExecutionPolicy Bypass -File again, and so the same words work from cmd, from PowerShell
    and from a double-click.

    Three problems it solves, all of which had to be worked around by hand before:

    1. STALE BUILD, NO ERROR. A second instance of Quiesce does not replace the first - the
       single-instance handshake signals the first to show its window and then exits silently.
       So building a change and launching it while the old build was still resident showed the
       OLD build, with nothing anywhere saying so. Since the notification-area icon landed, the
       old build is usually invisible rather than obviously open, which turns a rare confusion
       into the normal case. Stopping the running instance first is not tidiness; it is the only
       way to be sure which build you are looking at.

    2. A LOCKED STAGING DIRECTORY. Quiesce is elevated, so an unelevated shell cannot terminate
       it, and run-app.ps1 staged to one fixed path that it deleted with -ErrorAction
       SilentlyContinue before copying with $ErrorActionPreference = 'Stop'. A hidden instance
       therefore made the delete a no-op and the next copy an exception. Every run now stages to
       its own directory, so a locked one can never block the next run, and old ones are pruned
       best-effort afterwards.

    3. CLI VERBS THAT COULD NOT SEE THE MACHINE. quiesce.exe deliberately carries no
       requireAdministrator manifest - Quiesce.Cli.csproj says why: a WinExe that elevates has no
       console and no observable exit code, which would make CLI acceptance tests unrunnable. The
       cost is that every verb run from a normal shell reports on a data root it is not permitted
       to read. And Start-Process refuses to combine -Verb RunAs with -RedirectStandardOutput, so
       elevated output had nowhere to go. Verbs here run through a generated .cmd that redirects
       into a log, which is printed when it finishes and left at a stable path to re-read.

.EXAMPLE
    .\q.cmd                     # build and launch the app
    .\q.cmd inventory           # elevated, printed here and logged
    .\q.cmd restore
    .\q.cmd resync              # reports; add --apply to act
    .\q.cmd stop
    .\q.cmd test

.NOTES
    PowerShell 5.1 only on this machine; pwsh is not installed. No &&, no ternary, no ??.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Verb = 'app',

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]] $Rest = @(),

    [string] $Configuration = 'Release',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $env:TEMP 'quiesce-run'
$logDir  = Join-Path $runRoot 'logs'
$lastLog = Join-Path $logDir 'last.log'

# Every verb quiesce.exe answers to, kept in one place so an unknown word is refused here with a
# list rather than passed through to be refused there after a UAC prompt for nothing.
$cliVerbs = @(
    'inventory', 'print-plan', 'engage', 'restore', 'resync',
    'revert-all', 'recover', 'verify-revert', 'list-apps', 'list-startup'
)

# The verbs that change the machine. Named so the console says which kind of thing is about to
# happen BEFORE the elevation prompt, since the prompt itself only ever says "Windows Command
# Processor" and cannot tell the user whether they are about to read or to close their browsers.
$mutatingVerbs = @('engage', 'restore', 'revert-all', 'recover')

function Say    ($text) { Write-Host $text }
function Note   ($text) { Write-Host $text -ForegroundColor DarkGray }
function Head   ($text) { Write-Host ''; Write-Host $text -ForegroundColor Cyan }
function Warned ($text) { Write-Host $text -ForegroundColor Yellow }

function New-CmdFile {
    <#
      A generated .cmd, written ASCII on purpose: cmd.exe mishandles a UTF-8 byte-order mark and
      this environment's redirection defaults write one.
    #>
    param([string] $Path, [string[]] $Lines)
    Set-Content -LiteralPath $Path -Value $Lines -Encoding ascii
}

function Start-Elevated {
    <#
      Start-Process -Verb RunAs THROWS when the consent dialog is dismissed - it does not return a
      process with a non-zero exit code. Left unhandled that surfaces as a PowerShell stack trace
      pointing at this script, which reads like the launcher broke rather than like a prompt was
      declined. Returns $null for "declined", which every caller has to handle.
    #>
    param([string] $Path)
    try {
        return Start-Process -FilePath $Path -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    }
    catch [InvalidOperationException] {
        return $null
    }
}

function Get-RunningInstance {
    # Get-Process sees an elevated process by name from an unelevated shell; it is .Path that is
    # denied. So presence is knowable here, and only the killing needs elevation.
    return @(Get-Process -Name 'Quiesce' -ErrorAction SilentlyContinue)
}

function Invoke-Build {
    param([string] $ProjectPath)
    if ($NoBuild) { Note "  skipping build (-NoBuild)"; return }
    & dotnet build $ProjectPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

function Find-Output {
    param([string] $ProjectDir, [string] $Exe)
    $binDir = Join-Path $ProjectDir "bin\$Configuration"
    if (-not (Test-Path $binDir)) { throw "$binDir does not exist. Build first, or drop -NoBuild." }
    $found = Get-ChildItem $binDir -Recurse -Filter $Exe -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $found) { throw "$Exe not found under $binDir. Build first." }
    return $found
}

function Stop-Instance {
    <#
      Returns $true when it asked for a stop, $false when nothing was running. taskkill needs
      elevation because the target is elevated - there is no unelevated path to this, which is
      the whole reason the old workflow ended in a locked directory.
    #>
    $running = Get-RunningInstance
    if ($running.Count -eq 0) { return $false }

    Note "  stopping $($running.Count) running instance(s) - PID $($running.Id -join ', ')"
    New-Item -ItemType Directory -Force $runRoot | Out-Null
    $helper = Join-Path $runRoot 'stop.cmd'
    New-CmdFile $helper @(
        '@echo off',
        'taskkill /f /im Quiesce.exe >nul 2>&1',
        'exit /b 0'
    )
    if ($null -eq (Start-Elevated $helper)) {
        throw "The elevation prompt was declined, so the running instance is still up. Nothing was changed."
    }

    # Wait for teardown rather than assume it: the single-instance mutex is released by the OS
    # when the process dies, and launching into a mutex still held would silently show nothing.
    for ($i = 0; $i -lt 50; $i++) {
        if ((Get-RunningInstance).Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 100
    }
    throw "Quiesce is still running after a stop was requested. Try the tray menu's Exit."
}

function Invoke-App {
    Head 'Build'
    Invoke-Build (Join-Path $repo 'src\Quiesce.App\Quiesce.App.csproj')

    Head 'Stop whatever is already running'
    $stopped = Stop-Instance
    if (-not $stopped) { Note '  nothing was running' }

    Head 'Stage'
    # A directory per run. The previous fixed path was lockable by an instance the user could not
    # see, and a stage that cannot be prepared is a worse failure than a little disk in %TEMP%.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $stage = Join-Path $runRoot $stamp
    New-Item -ItemType Directory -Force $stage | Out-Null

    $source = Find-Output (Join-Path $repo 'src\Quiesce.App') 'Quiesce.exe'
    Copy-Item (Join-Path $source.DirectoryName '*') $stage -Recurse -Force
    # The catalog travels with the build so resolution behaves like an installed layout.
    Copy-Item (Join-Path $repo 'catalog') $stage -Recurse -Force
    Note "  $stage"

    # Best-effort, and silent about failures on purpose: a stage still held by something is not a
    # reason to refuse to launch the build we just made.
    Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $stage -and $_.Name -ne 'logs' } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    Head 'Launch'
    Note '  expect one elevation prompt - the app is requireAdministrator'
    Start-Process (Join-Path $stage 'Quiesce.exe')
    Say  "  launched $stamp"
    Note '  quit from the tray menu, or just run this again - it stops the old one for you'
}

function Invoke-Cli {
    param([string] $Name, [string[]] $Arguments)

    Head 'Build'
    Invoke-Build (Join-Path $repo 'src\Quiesce.Cli\Quiesce.Cli.csproj')
    $exe = Find-Output (Join-Path $repo 'src\Quiesce.Cli') 'quiesce.exe'

    New-Item -ItemType Directory -Force $logDir | Out-Null
    if (Test-Path $lastLog) { Remove-Item $lastLog -Force -ErrorAction SilentlyContinue }

    $argLine = @($Name) + $Arguments -join ' '

    Head "quiesce $argLine"
    if ($mutatingVerbs -contains $Name -or ($Name -eq 'resync' -and $Arguments -contains '--apply')) {
        Warned "  this verb CHANGES THIS MACHINE. engage closes browsers and nothing reopens them."
    }
    Note '  elevated, because the data root is hardened to Administrators and an unelevated'
    Note '  read of it returns a misleading false rather than an error'

    $helper = Join-Path $runRoot 'cli.cmd'
    New-Item -ItemType Directory -Force $runRoot | Out-Null
    New-CmdFile $helper @(
        '@echo off',
        ('"' + $exe.FullName + '" ' + $argLine + ' > "' + $lastLog + '" 2>&1'),
        'exit /b %ERRORLEVEL%'
    )

    $proc = Start-Elevated $helper
    if ($null -eq $proc) {
        Write-Host ''
        Warned '  the elevation prompt was declined, so nothing ran and nothing changed.'
        Note  '  every verb here needs it: the data root is Administrators-only, and an unelevated'
        Note  '  read of it does not fail - it quietly reports a clean machine that may be engaged.'
        return 3
    }
    $code = $proc.ExitCode

    Write-Host ''
    if (Test-Path $lastLog) {
        Get-Content $lastLog | ForEach-Object { Write-Host "  $_" }
        Copy-Item $lastLog (Join-Path $logDir ("$Name-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')) -Force
    }
    else {
        Warned '  the verb ran but wrote no output, which should not happen. Check the log path below.'
    }

    Write-Host ''
    Note "  exit code $code    log: $lastLog"
    return $code
}

function Invoke-Script {
    <#
      Runs one of this repo's own elevated scripts through the same redirect-to-a-log helper the CLI
      verbs use, so its output is readable from the shell that asked for it rather than flashing past
      in a console that closes.
    #>
    param([string] $ScriptName, [string[]] $Arguments)

    $script = Join-Path $PSScriptRoot $ScriptName
    if (-not (Test-Path $script)) { throw "$script not found." }

    New-Item -ItemType Directory -Force $logDir | Out-Null
    if (Test-Path $lastLog) { Remove-Item $lastLog -Force -ErrorAction SilentlyContinue }

    Head ($ScriptName + ' ' + ($Arguments -join ' '))
    Note '  elevated, because the data root is hardened to Administrators'

    $helper = Join-Path $runRoot 'script.cmd'
    New-CmdFile $helper @(
        '@echo off',
        ('powershell -NoProfile -ExecutionPolicy Bypass -File "' + $script + '" ' +
         ($Arguments -join ' ') + ' > "' + $lastLog + '" 2>&1'),
        'exit /b %ERRORLEVEL%'
    )

    $proc = Start-Elevated $helper
    if ($null -eq $proc) {
        Write-Host ''
        Warned '  the elevation prompt was declined, so nothing ran and nothing changed.'
        return 3
    }

    Write-Host ''
    if (Test-Path $lastLog) {
        Get-Content $lastLog | ForEach-Object { Write-Host "  $_" }
        Copy-Item $lastLog (Join-Path $logDir ($ScriptName + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')) -Force
    }
    Write-Host ''
    Note "  exit code $($proc.ExitCode)    log: $lastLog"
    return $proc.ExitCode
}

function Invoke-Test {
    Head 'dotnet test'
    & dotnet test (Join-Path $repo 'Quiesce.slnx') --nologo
    return $LASTEXITCODE
}

function Show-Help {
    Say ''
    Say 'q - build, launch and drive Quiesce'
    Say ''
    Say '  q                     build and launch the app (stops the running one first)'
    Say '  q app                 same'
    Say '  q stop                stop the running instance'
    Say '  q build               build without launching'
    Say '  q test                run the test suite'
    Say ''
    Say '  q inventory           what Quiesce thinks this machine is, including drift'
    Say '  q print-plan          what an engage would do, without doing it'
    Say '  q resync              report drift; add --apply to act on it'
    Say '  q restore             put back everything the active session applied'
    Say '  q revert-all          put back every session, not only the active one'
    Say '  q recover             what the boot-time recovery path would do'
    Say '  q verify-revert       check the generated revert.cmd against the journal'
    Say '  q list-apps           what the app-close proposal would offer'
    Say '  q list-startup        what the sign-in list would offer'
    Say ''
    Say '  q clean-slate         reset Quiesce''s bookkeeping, keeping the entries you authored.'
    Say '                        Refuses while engaged - Restore first. -WhatIfOnly to preview.'
    Say '  q verify-m7           walk the drift-and-resync feature end to end'
    Say ''
    Note '  CLI verbs run elevated and their output is printed here and kept at'
    Note "  $lastLog"
    Say ''
    Say '  -Configuration Debug  build Debug instead of Release'
    Say '  -NoBuild              use whatever is already in bin'
    Say ''
    return 0
}

switch ($Verb.ToLowerInvariant()) {
    'app'     { Invoke-App; exit 0 }
    ''        { Invoke-App; exit 0 }
    'stop'    {
        Head 'Stop'
        $stopped = Stop-Instance
        if ($stopped) { Say '  stopped' } else { Say '  nothing was running' }
        exit 0
    }
    'build' {
        Head 'Build'
        Invoke-Build (Join-Path $repo 'Quiesce.slnx')
        exit 0
    }
    'test'    { exit (Invoke-Test) }
    'clean-slate' { exit (Invoke-Script 'clean-slate.ps1' $Rest) }
    'verify-m7'   { exit (Invoke-Script 'verify-m7.ps1' $Rest) }
    'help'    { exit (Show-Help) }
    '--help'  { exit (Show-Help) }
    '-h'      { exit (Show-Help) }
    '/?'      { exit (Show-Help) }
    default {
        if ($cliVerbs -contains $Verb.ToLowerInvariant()) {
            exit (Invoke-Cli $Verb.ToLowerInvariant() $Rest)
        }
        Write-Host "q: unknown command '$Verb'." -ForegroundColor Red
        Show-Help | Out-Null
        exit 2
    }
}
