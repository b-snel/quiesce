<#
.SYNOPSIS
    Works out WHY a registry write is refused, when the DACL says it should not be.

.DESCRIPTION
    reg.exe and Quiesce are both refused writing
    HKLM\SOFTWARE\Policies\Microsoft\Dsh!AllowNewsAndInterests from an elevated process, on a key
    where BUILTIN\Administrators holds FullControl, while sibling keys under
    SOFTWARE\Policies\Microsoft accept writes in the same session.

    PART 1 separates a permissions problem from an operation veto. A DACL problem fails at the OPEN
    when SetValue rights are requested; a kernel registry callback filters RegNtPreSetValueKey, not
    the open, so the open succeeds and the write then fails.

    Result on the target machine: every right opens, and a scratch value writes and deletes
    cleanly. So the key is writable and the veto is specific to something about the write itself.

    PART 2 finds which axis the veto keys on - value name, data, type, or key - by writing a matrix
    of scratch values through handles that opened successfully. Everything it manages to write, it
    deletes.

    PART 3 looks for who logged the block. Defender records tamper vetoes as event 5013 in
    Microsoft-Windows-Windows Defender/Operational, which turns "something is vetoing this" into a
    named culprit.

    Only writes values with obviously-temporary names, plus the one real value under test, and
    removes everything it created.
#>
[CmdletBinding()]
param(
    [string] $Target  = 'SOFTWARE\Policies\Microsoft\Dsh',
    [string] $Control = 'SOFTWARE\Policies\Microsoft\Windows\CloudContent',
    [string] $ValueUnderTest = 'AllowNewsAndInterests',
    [string] $ScratchValue = 'QuiesceDiagnosticScratch'
)

$ErrorActionPreference = 'Continue'

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "elevated: $elevated"
if (-not $elevated) { Write-Host "Run this from an elevated prompt or the results mean nothing." -ForegroundColor Red; exit 2 }
$startedAt = Get-Date
Write-Host ""

function Test-KeyAccess {
    param([string] $SubKey, [string] $Label)

    Write-Host "=== $Label : HKLM\$SubKey ===" -ForegroundColor Cyan

    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        $probe = $base.OpenSubKey($SubKey, $false)
        if (-not $probe) { Write-Host "  key does not exist"; return }
        $probe.Close()
    } catch { Write-Host "  cannot even open for read: $($_.Exception.Message)"; return }

    foreach ($right in 'ReadKey', 'QueryValues', 'SetValue', 'CreateSubKey', 'WriteKey', 'ReadPermissions', 'ChangePermissions', 'TakeOwnership') {
        try {
            $k = $base.OpenSubKey($SubKey, 'ReadSubTree', ([Enum]::Parse([System.Security.AccessControl.RegistryRights], $right)))
            if ($k) { Write-Host ("  open {0,-18} OK" -f $right) -ForegroundColor Green; $k.Close() }
            else    { Write-Host ("  open {0,-18} returned null" -f $right) -ForegroundColor Yellow }
        } catch {
            Write-Host ("  open {0,-18} DENIED  ({1})" -f $right, $_.Exception.GetType().Name) -ForegroundColor Red
        }
    }

    $acl = Get-Acl "HKLM:\$SubKey"
    Write-Host "  owner: $($acl.Owner)   DACL protected from inheritance: $($acl.AreAccessRulesProtected)"
    $acl.Access | Where-Object { $_.AccessControlType -eq 'Deny' } | ForEach-Object {
        Write-Host "  DENY ACE: $($_.IdentityReference) $($_.RegistryRights)" -ForegroundColor Red
    }
    Write-Host ""
}

<#
Writes one value through a handle opened with SetValue rights and reports the outcome, then removes
anything it managed to write. The point is the CONTRAST between rows: the axis along which the
outcome flips is the axis the veto keys on.
#>
function Test-ValueWrite {
    param([string] $SubKey, [string] $Name, $Data, [string] $Kind, [string] $Note)

    # Existence is read through a SEPARATE read-only handle. A handle opened with SetValue rights
    # alone cannot enumerate - GetValueNames() throws UnauthorizedAccessException on it - and the
    # first version let that exception fall through under ErrorActionPreference=Continue, leaving
    # $existedBefore null and the cleanup branch willing to DELETE a value that was already there.
    # A diagnostic that destroys the state it is diagnosing is worse than no diagnostic.
    $existedBefore = $false
    try { $existedBefore = (Get-Item "HKLM:\$SubKey" -ErrorAction Stop).GetValueNames() -contains $Name } catch { }

    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    $k = $null
    try { $k = $base.OpenSubKey($SubKey, 'ReadWriteSubTree', [System.Security.AccessControl.RegistryRights]::SetValue) } catch { }
    if (-not $k) { Write-Host ("  {0,-52} NO HANDLE" -f $Note) -ForegroundColor Red; return }

    if ($existedBefore) { Write-Host ("  {0,-52} SKIPPED - value already exists, not overwriting" -f $Note) -ForegroundColor Yellow; $k.Close(); return }
    try {
        $k.SetValue($Name, $Data, $Kind)
        Write-Host ("  {0,-52} WROTE OK" -f $Note) -ForegroundColor Green
        try { $k.DeleteValue($Name, $false) } catch { Write-Host "      cleanup FAILED for $Name - remove by hand" -ForegroundColor Red }
    } catch {
        Write-Host ("  {0,-52} DENIED  0x{1:X8}" -f $Note, $_.Exception.HResult) -ForegroundColor Red
        Write-Host ("      {0}" -f $_.Exception.Message) -ForegroundColor DarkRed
    } finally { $k.Close() }
}

Test-KeyAccess -SubKey $Target  -Label 'TARGET (refuses the real write)'
Test-KeyAccess -SubKey $Control -Label 'CONTROL (accepted writes)'

Write-Host "=== value axis: what is the veto actually keyed on? ===" -ForegroundColor Cyan
Test-ValueWrite -SubKey $Target  -Name $ScratchValue          -Data 1 -Kind DWord  -Note 'target key, unrelated name           (baseline)'
Test-ValueWrite -SubKey $Target  -Name $ValueUnderTest        -Data 0 -Kind DWord  -Note 'target key, real name, DWORD 0        (the failure)'
Test-ValueWrite -SubKey $Target  -Name $ValueUnderTest        -Data 1 -Kind DWord  -Note 'target key, real name, DWORD 1        (data-keyed?)'
Test-ValueWrite -SubKey $Target  -Name $ValueUnderTest        -Data 'x' -Kind String -Note 'target key, real name, REG_SZ        (type-keyed?)'
Test-ValueWrite -SubKey $Target  -Name $ValueUnderTest.ToLower() -Data 0 -Kind DWord -Note 'target key, lowercased name          (case-keyed?)'
Test-ValueWrite -SubKey $Target  -Name "$ValueUnderTest`Z"    -Data 0 -Kind DWord  -Note 'target key, name + suffix             (prefix match?)'
Test-ValueWrite -SubKey $Control -Name $ValueUnderTest        -Data 0 -Kind DWord  -Note 'CONTROL key, real name                (key- or name-scoped?)'
Write-Host ""

Write-Host "=== who logged the block? ===" -ForegroundColor Cyan
# Defender records a tamper veto as 5013 and a settings change as 5007. If tamper protection is
# responsible, this names it outright instead of leaving it as the most plausible guess.
# No Id filter on the Defender log: a 7-day sweep found no 5013 at all, which already argues
# against tamper protection, so the useful question is now "did ANYTHING log around the attempt"
# rather than "did the event I expected appear".
foreach ($log in 'Microsoft-Windows-Windows Defender/Operational', 'Microsoft-Windows-AppLocker/MSI and Script', 'Security') {
    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $startedAt.AddMinutes(-30) } -ErrorAction Stop |
                  Where-Object { $log -notlike '*Security*' -or $_.Id -eq 4657 } | Select-Object -First 8
        if (-not $events) { Write-Host "  $log : nothing relevant in the last 30 min" }
        foreach ($e in $events) {
            Write-Host "  $log  id=$($e.Id)  $($e.TimeCreated)" -ForegroundColor Yellow
            Write-Host "    $(($e.Message -split "`n" | Select-Object -First 4) -join ' | ')" -ForegroundColor Yellow
        }
    } catch { Write-Host "  $log : $($_.Exception.Message)" }
}
Write-Host ""

Write-Host "=== context ===" -ForegroundColor Cyan
try { Write-Host "  Defender tamper protection : $((Get-MpComputerStatus -ErrorAction Stop).IsTamperProtected)" }
catch { Write-Host "  Defender status unavailable: $($_.Exception.Message)" }

$csp = 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\NewsAndInterests'
Write-Host "  Policy CSP NewsAndInterests : $(if (Test-Path $csp) { ((Get-Item $csp).GetValueNames() | ForEach-Object { "$_=$((Get-Item $csp).GetValue($_))" }) -join ', ' } else { '<not present>' })"
foreach ($p in 'HKLM:\SOFTWARE\Microsoft\PolicyManager\providers', 'HKLM:\SOFTWARE\Microsoft\Enrollments') {
    $n = if (Test-Path $p) { @(Get-ChildItem $p -ErrorAction SilentlyContinue).Count } else { 0 }
    Write-Host "  $p : $n subkey(s)"
}
try {
    Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop |
        ForEach-Object { Write-Host "  AV registered: $($_.displayName)" }
} catch { Write-Host "  AV enumeration unavailable: $($_.Exception.Message)" }

Write-Host ""
Write-Host "Leftover scratch check:" -ForegroundColor Cyan
foreach ($k in $Target, $Control) {
    $names = @((Get-Item "HKLM:\$k" -ErrorAction SilentlyContinue).GetValueNames() |
               Where-Object { $_ -like "*$ScratchValue*" -or $_ -like "*$ValueUnderTest*" })
    Write-Host "  HKLM\$k : $(if ($names) { $names -join ', ' } else { 'clean' })"
}
