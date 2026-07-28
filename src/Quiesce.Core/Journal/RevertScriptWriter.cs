using System.Globalization;
using System.Text;
using Quiesce.Core.Platform;

namespace Quiesce.Core.Journal;

/// <summary>
/// Emits a plain <c>revert.cmd</c> of literal <c>reg.exe</c> commands beside the journal.
/// </summary>
/// <remarks>
/// This is recovery net 4, and the only one that needs no Quiesce binary at all. It matters
/// because the app is Authenticode-unsigned and a program that rewrites the registry and (later)
/// reconfigures services is a behavioural match for defense-evasion tooling — Defender can
/// quarantine the panic button precisely when it is needed. A .cmd of reg.exe calls survives that.
/// <para>
/// It is a genuine net, not documentation: the file is written before the first mutation and
/// appended as each step is journaled, so a machine that loses power mid-apply still has an
/// executable undo on disk covering everything that was actually done.
/// </para>
/// <para>
/// Deliberately NOT a <c>.reg</c> file as the primary mechanism: <c>reg import</c> merges, so it
/// can restore a changed value but can never remove one that did not exist before — which is the
/// single most common prior state Quiesce captures.
/// </para>
/// </remarks>
public sealed class RevertScriptWriter : IDisposable
{
    private readonly StreamWriter _writer;

    private RevertScriptWriter(StreamWriter writer) => _writer = writer;

    public static RevertScriptWriter Create(string sessionDir, Guid sessionId)
    {
        Directory.CreateDirectory(sessionDir);

        var stream = new FileStream(
            Path.Combine(sessionDir, "revert.cmd"),
            FileMode.Create, FileAccess.Write, FileShare.Read);

        // ASCII, CRLF: cmd.exe is unforgiving about both. A UTF-8 BOM makes the first line fail.
        var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

        writer.WriteLine("@echo off");
        writer.WriteLine("REM ============================================================");
        writer.WriteLine("REM  Quiesce emergency revert");
        writer.WriteLine($"REM  session {sessionId:D}");
        writer.WriteLine("REM");
        writer.WriteLine("REM  Undoes this Quiesce session using only reg.exe. Run as");
        writer.WriteLine("REM  Administrator. Needs no Quiesce install and no catalog - use it if");
        writer.WriteLine("REM  the app is broken, uninstalled, or quarantined by antivirus.");
        writer.WriteLine("REM");
        writer.WriteLine("REM  Written before each change is made, so it is safe to run after a");
        writer.WriteLine("REM  crash or power loss mid-apply.");
        writer.WriteLine("REM ============================================================");
        writer.WriteLine();
        writer.WriteLine("setlocal");
        writer.WriteLine("set QUIESCE_FAILED=0");
        writer.WriteLine();

        return new RevertScriptWriter(writer);
    }

    /// <summary>
    /// Appends the inverse of one applied step. Called immediately after the step is journaled,
    /// so the script is never behind the machine.
    /// </summary>
    public void AppendInverse(int stepId, RegistryTarget target, RegistryProbe prior)
    {
        var key = FormatKeyPath(target);

        _writer.WriteLine($"REM --- step {stepId}: {Describe(prior)}");

        switch (prior.Presence)
        {
            case RegPresence.ValuePresent when prior.Value is { } value:
                _writer.WriteLine(
                    $"reg add \"{key}\" /v \"{target.ValueName}\" /t {RegExeType(value.ValueKind)} " +
                    $"/d {QuoteData(value)} /f");
                break;

            case RegPresence.ValueAbsent:
            case RegPresence.KeyAbsent:
                // Delete, not "write 0". This is the whole point of the tri-state prior, and the
                // reason reg import alone could never serve as the recovery net.
                _writer.WriteLine($"reg delete \"{key}\" /v \"{target.ValueName}\" /f >nul 2>&1");

                if (prior.Presence == RegPresence.KeyAbsent && prior.MissingKeyPath is { } created)
                {
                    // Only the keys Quiesce created, deepest first. reg delete on a key removes its
                    // whole subtree, so this runs only for keys that did not exist beforehand.
                    var segments = key.Split('\\');
                    var createdDepth = created.Split('\\').Length;

                    for (var depth = segments.Length; depth > segments.Length - createdDepth; depth--)
                    {
                        _writer.WriteLine($"reg delete \"{string.Join('\\', segments.Take(depth))}\" /f >nul 2>&1");
                    }
                }

                break;

            default:
                _writer.WriteLine($"REM  (unknown prior presence '{prior.Presence}' - skipped)");
                break;
        }

        _writer.WriteLine("if errorlevel 1 set QUIESCE_FAILED=1");
        _writer.WriteLine();
    }

    /// <summary>
    /// Appends the inverse of a service change as literal <c>sc.exe</c> commands.
    /// </summary>
    /// <remarks>
    /// Start type and the delayed-auto flag are restored as separate facts, in that order, because
    /// <c>sc config start= delayed-auto</c> only means anything on top of an automatic start — and
    /// a service that was Automatic-Delayed and comes back plain Automatic silently slows every
    /// subsequent boot. The service is started again only if it was actually running.
    /// </remarks>
    public void AppendServiceInverse(int stepId, ServiceSnapshot prior)
    {
        _writer.WriteLine($"REM --- step {stepId}: restore service {prior.Service}");

        if (!prior.Present || prior.StartType is not { } startType)
        {
            _writer.WriteLine("REM  (service was not present; nothing to restore)");
            _writer.WriteLine();
            return;
        }

        var startArg = startType switch
        {
            ServiceStartType.Automatic when prior.DelayedAutostart => "delayed-auto",
            ServiceStartType.Automatic => "auto",
            ServiceStartType.Manual => "demand",
            ServiceStartType.Disabled => "disabled",
            ServiceStartType.Boot => "boot",
            ServiceStartType.System => "system",
            _ => null,
        };

        if (startArg is null)
        {
            _writer.WriteLine($"REM  (unknown prior start type '{startType}' - restore by hand)");
            _writer.WriteLine();
            return;
        }

        // sc.exe is famously picky: the space after "start=" is required.
        _writer.WriteLine($"sc config \"{prior.Service}\" start= {startArg}");
        _writer.WriteLine("if errorlevel 1 set QUIESCE_FAILED=1");

        if (prior.RunState == ServiceRunState.Running)
        {
            _writer.WriteLine($"sc start \"{prior.Service}\" >nul 2>&1");
            _writer.WriteLine("REM  (a start failure here is not fatal: the service may start on demand)");
        }

        _writer.WriteLine();
    }

    /// <summary>
    /// Appends the inverse of a power scheme switch as a literal <c>powercfg</c> command.
    /// </summary>
    /// <remarks>
    /// The cleanest line in this whole file, and worth saying why: the prior is one GUID, so the undo is
    /// one command with no ordering, no flags and no conditionals. It also needs no elevation — measured
    /// on this machine, <c>powercfg /setactive</c> succeeds as a standard interactive user — so this is
    /// the one step of the emergency script that still works if the user cannot get an admin prompt.
    /// <para>
    /// The scheme is written as a GUID, never as a name: <c>powercfg /setactive</c> accepts an alias like
    /// <c>SCHEME_BALANCED</c>, but only for the schemes Microsoft ships, and the friendly name is
    /// localized. The GUID is the identity everywhere else in this feature and it is the identity here.
    /// </para>
    /// </remarks>
    public void AppendPowerInverse(int stepId, PowerPrior prior)
    {
        ArgumentNullException.ThrowIfNull(prior);

        _writer.WriteLine($"REM --- step {stepId}: restore power plan {Ascii(prior.FriendlyName) ?? "(name unknown)"}");

        if (!prior.Readable)
        {
            _writer.WriteLine("REM  (the previous plan could not be read; choose one in Windows)");
            _writer.WriteLine();
            return;
        }

        _writer.WriteLine($"powercfg /setactive {prior.Scheme:D}");
        _writer.WriteLine("if errorlevel 1 set QUIESCE_FAILED=1");
        _writer.WriteLine();
    }

    /// <summary>
    /// Strips anything this ASCII, CRLF file cannot represent.
    /// </summary>
    /// <remarks>
    /// Power scheme names are localized, so on a non-English Windows they arrive as text an ASCII
    /// StreamWriter turns into '?' — and this string only ever appears inside a REM comment, where a
    /// mangled character is cosmetic. Replaced explicitly rather than left to the encoder so the comment
    /// reads as a deliberate omission rather than as corruption.
    /// </remarks>
    private static string? Ascii(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return raw.All(char.IsAscii) ? raw : "(name omitted: not representable here)";
    }

    /// <summary>
    /// Records a process step as a comment, because there is no safe command to emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only step kind this script cannot undo, and it says so rather than emitting something that
    /// looks like an undo. A close has no inverse at all. A throttle technically does — PowerShell can
    /// set a priority class by PID — but this file exists to be run after a crash, a reboot, or an
    /// uninstall, and by then the PID has almost certainly been reused. Writing a priority onto whatever
    /// process inherited the number is a worse outcome than writing nothing, and this script has no way
    /// to check a creation time.
    /// </para>
    /// <para>
    /// The reassuring part is worth stating in the file: a priority class is not persistent. It is gone
    /// when the process exits and gone after a reboot, so a throttle Quiesce failed to undo costs the
    /// user a restart of that application and nothing else.
    /// </para>
    /// </remarks>
    public void AppendProcessNote(int stepId, ProcessPrior prior, Catalog.ProcessAction action, string? intended)
    {
        _writer.WriteLine($"REM --- step {stepId}: process {prior.ImageName} (pid {prior.Pid})");

        if (action == Catalog.ProcessAction.Close)
        {
            _writer.WriteLine("REM  (asked to close. Nothing here can reopen it, and neither can Quiesce -");
            _writer.WriteLine("REM   relaunching would mean guessing the command line, and for a browser it");
            _writer.WriteLine("REM   would restore the window without the tabs. Reopen it yourself.)");
        }
        else
        {
            _writer.WriteLine($"REM  (priority lowered to {intended ?? "a lower class"}, was {prior.PriorityClass}.");
            _writer.WriteLine("REM   Not restored here: this script runs by PID, and after a crash or reboot that");
            _writer.WriteLine("REM   number belongs to something else. A priority class does not survive the");
            _writer.WriteLine("REM   process exiting, so restarting the application clears it - or run");
            _writer.WriteLine("REM   'quiesce revert-all', which checks the process is still the same one.)");
        }

        _writer.WriteLine();
    }

    /// <summary>Appends a human note (e.g. an activation the script cannot replay).</summary>
    public void AppendNote(string note) => _writer.WriteLine($"REM  NOTE: {note}");

    public void Finish()
    {
        _writer.WriteLine("if \"%QUIESCE_FAILED%\"==\"1\" (");
        _writer.WriteLine("  echo.");
        _writer.WriteLine("  echo One or more steps failed. Re-run from an elevated prompt.");
        _writer.WriteLine("  exit /b 1");
        _writer.WriteLine(")");
        _writer.WriteLine("echo Quiesce session reverted.");
        _writer.WriteLine("exit /b 0");
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// Renders a target as a path reg.exe understands. Per-user targets become
    /// <c>HKU\&lt;sid&gt;\...</c> rather than <c>HKCU</c>, so the script restores the right user's
    /// hive even when run from another account or an elevated shell.
    /// </summary>
    private static string FormatKeyPath(RegistryTarget target) => target.Hive switch
    {
        "HKU" => $@"HKU\{target.UserSid}\{target.Subkey}",
        "HKLM" => $@"HKLM\{target.Subkey}",
        _ => throw new ArgumentException($"Unsupported hive '{target.Hive}'."),
    };

    private static string RegExeType(Microsoft.Win32.RegistryValueKind kind) => kind switch
    {
        Microsoft.Win32.RegistryValueKind.DWord => "REG_DWORD",
        Microsoft.Win32.RegistryValueKind.QWord => "REG_QWORD",
        Microsoft.Win32.RegistryValueKind.String => "REG_SZ",
        Microsoft.Win32.RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        Microsoft.Win32.RegistryValueKind.MultiString => "REG_MULTI_SZ",
        Microsoft.Win32.RegistryValueKind.Binary => "REG_BINARY",
        _ => throw new ArgumentException($"Unsupported registry kind '{kind}'."),
    };

    private static string QuoteData(RegistryData value)
    {
        var clr = value.ToClrValue();

        return value.ValueKind switch
        {
            Microsoft.Win32.RegistryValueKind.DWord =>
                "0x" + ((uint)(int)clr).ToString("x", CultureInfo.InvariantCulture),
            Microsoft.Win32.RegistryValueKind.QWord =>
                "0x" + ((ulong)(long)clr).ToString("x", CultureInfo.InvariantCulture),
            Microsoft.Win32.RegistryValueKind.Binary =>
                Convert.ToHexString((byte[])clr),
            // reg.exe joins REG_MULTI_SZ elements with \0.
            Microsoft.Win32.RegistryValueKind.MultiString =>
                Quote(string.Join("\\0", (string[])clr)),
            _ => Quote((string)clr),
        };
    }

    /// <summary>
    /// Quotes for cmd.exe. Registry data is machine-supplied rather than user-typed, but this
    /// script runs elevated, so unbalanced quotes are treated as a correctness bug either way.
    /// </summary>
    private static string Quote(string raw) => $"\"{raw.Replace("\"", "\\\"")}\"";

    private static string Describe(RegistryProbe prior) => prior.Presence switch
    {
        RegPresence.ValuePresent => $"restore {prior.Value!.Kind}",
        RegPresence.ValueAbsent => "value did not exist - delete it",
        RegPresence.KeyAbsent => "key did not exist - delete value and the keys we created",
        _ => "unknown",
    };
}
