<#
.SYNOPSIS
    Works out WHY a registry write is refused, when the DACL says it should not be.

.DESCRIPTION
    reg.exe and Quiesce both get "Access is denied" writing
    HKLM\SOFTWARE\Policies\Microsoft\Dsh!AllowNewsAndInterests, on a key where BUILTIN\Administrators
    holds FullControl, from an elevated process, while sibling keys under
    SOFTWARE\Policies\Microsoft accept writes in the same session.

    Two explanations remain, and they are distinguishable:

      - The DACL is not what it appears (effective rights differ from the listed ACEs). Then the
        OPEN fails when SetValue rights are requested.
      - A kernel registry callback is vetoing the operation. A callback filters
        RegNtPreSetValueKey, not the open, so the OPEN SUCCEEDS and the write then fails.

    So: open with escalating rights, then attempt a scratch write through a successfully opened
    handle. Whichever step fails names the cause.

    A sibling key that demonstrably accepted a write runs the identical sequence as a control, so a
    failure can be attributed to the target rather than to this script.

    Creates only a scratch value with an obviously-temporary name, and deletes it. Nothing else is
    written.
#>
[CmdletBinding()]
param(
    [string] $Target  = 'SOFTWARE\Policies\Microsoft\Dsh',
    [string] $Control = 'SOFTWARE\Policies\Microsoft\Windows\CloudContent',
    [string] $ScratchValue = 'QuiesceDiagnosticScratch'
)

$ErrorActionPreference = 'Continue'

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "elevated: $elevated"
if (-not $elevated) { Write-Host "Run this from an elevated prompt or the results mean nothing." -ForegroundColor Red; exit 2 }
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

    # Which specific right is refused at OPEN time. A DACL problem shows up here.
    foreach ($right in 'ReadKey', 'QueryValues', 'SetValue', 'CreateSubKey', 'WriteKey', 'ReadPermissions', 'ChangePermissions', 'TakeOwnership') {
        try {
            $k = $base.OpenSubKey($SubKey, 'ReadSubTree', ([Enum]::Parse([System.Security.AccessControl.RegistryRights], $right)))
            if ($k) { Write-Host ("  open {0,-18} OK" -f $right) -ForegroundColor Green; $k.Close() }
            else    { Write-Host ("  open {0,-18} returned null" -f $right) -ForegroundColor Yellow }
        } catch {
            Write-Host ("  open {0,-18} DENIED  ({1})" -f $right, $_.Exception.GetType().Name) -ForegroundColor Red
        }
    }

    # The discriminator: write through a handle that opened successfully with SetValue rights.
    # Success here and failure in reg.exe would mean the problem is the caller, not the key.
    # Failure here, after a clean open, means something is vetoing the operation itself.
    try {
        $k = $base.OpenSubKey($SubKey, 'ReadWriteSubTree', [System.Security.AccessControl.RegistryRights]::SetValue)
        if (-not $k) {
            Write-Host "  WRITE: could not obtain a SetValue handle" -ForegroundColor Red
        } else {
            try {
                $k.SetValue($ScratchValue, 1, 'DWord')
                Write-Host "  WRITE: scratch value written OK - the key accepts writes" -ForegroundColor Green
                try { $k.DeleteValue($ScratchValue, $false); Write-Host "  cleanup: scratch value removed" } catch { Write-Host "  cleanup FAILED - remove $ScratchValue by hand" -ForegroundColor Red }
            } catch {
                Write-Host "  WRITE: DENIED through a successfully opened handle" -ForegroundColor Red
                Write-Host "         $($_.Exception.Message)" -ForegroundColor Red
                Write-Host "         HRESULT 0x$('{0:X8}' -f $_.Exception.HResult)" -ForegroundColor Red
                Write-Host "         => the open succeeded, so this is not the DACL. Something is" -ForegroundColor Red
                Write-Host "            vetoing the set-value operation itself." -ForegroundColor Red
            }
            $k.Close()
        }
    } catch {
        Write-Host "  WRITE: could not open with SetValue rights: $($_.Exception.Message)" -ForegroundColor Red
    }

    $acl = Get-Acl "HKLM:\$SubKey"
    Write-Host "  owner: $($acl.Owner)"
    Write-Host "  DACL protected from inheritance: $($acl.AreAccessRulesProtected)"
    $acl.Access | Where-Object { $_.AccessControlType -eq 'Deny' } | ForEach-Object {
        Write-Host "  DENY ACE: $($_.IdentityReference) $($_.RegistryRights)" -ForegroundColor Red
    }
    Write-Host ""
}

Test-KeyAccess -SubKey $Target  -Label 'TARGET (refuses writes)'
Test-KeyAccess -SubKey $Control -Label 'CONTROL (accepted writes)'

Write-Host "=== context ===" -ForegroundColor Cyan
try {
    $mp = Get-MpComputerStatus -ErrorAction Stop
    Write-Host "  Defender tamper protection : $($mp.IsTamperProtected)"
} catch { Write-Host "  Defender status unavailable: $($_.Exception.Message)" }

# A Policy CSP / MDM-managed value is the standard reason a Policies key stops accepting direct
# writes: the CSP owns it and the direct path is closed off.
$csp = 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\NewsAndInterests'
Write-Host "  Policy CSP NewsAndInterests : $(if (Test-Path $csp) { ((Get-Item $csp).GetValueNames() | ForEach-Object { "$_=$((Get-Item $csp).GetValue($_))" }) -join ', ' } else { '<not present>' })"
foreach ($p in 'HKLM:\SOFTWARE\Microsoft\PolicyManager\providers', 'HKLM:\SOFTWARE\Microsoft\Enrollments') {
    $n = if (Test-Path $p) { @(Get-ChildItem $p -ErrorAction SilentlyContinue).Count } else { 0 }
    Write-Host "  $p : $n subkey(s)"
}

# A registry callback belongs to some product. Naming the candidates turns "something is vetoing
# this" into something that can actually be chased.
try {
    Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop |
        ForEach-Object { Write-Host "  AV registered: $($_.displayName)" }
} catch { Write-Host "  AV enumeration unavailable: $($_.Exception.Message)" }
