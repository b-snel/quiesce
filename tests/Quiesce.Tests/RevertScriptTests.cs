using System.Text.Json;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// Tests for recovery net 4 — the reg.exe script that undoes a session with no Quiesce binary.
/// </summary>
/// <remarks>
/// This net exists for the case where the app itself is gone (quarantined, uninstalled, broken), so
/// its correctness cannot be checked by running Quiesce. Asserting on the emitted text is the only
/// verification available, which makes these tests load-bearing rather than incidental.
/// </remarks>
public class RevertScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quiesce-script-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Emit(Action<RevertScriptWriter> body)
    {
        using (var writer = RevertScriptWriter.Create(_dir, Guid.NewGuid()))
        {
            body(writer);
            writer.Finish();
        }

        return File.ReadAllText(Path.Combine(_dir, "revert.cmd"));
    }

    private static RegistryTarget UserTarget(string subkey = @"SOFTWARE\Quiesce\T", string value = "V") => new()
    {
        Hive = "HKU",
        UserSid = EngineTestHarness.Sid,
        Subkey = subkey,
        ValueName = value,
    };

    [Fact]
    public void Absent_value_becomes_a_delete_not_a_zero_write()
    {
        // The whole reason a .reg file cannot serve as the recovery net: reg import merges, so it
        // can never remove a value that did not exist before.
        var script = Emit(w => w.AppendInverse(1, UserTarget(), new RegistryProbe { Presence = RegPresence.ValueAbsent }));

        Assert.Contains("reg delete", script);
        Assert.DoesNotContain("reg add", script);
    }

    [Fact]
    public void Present_value_is_restored_with_its_original_type()
    {
        // A REG_SZ target restored as REG_DWORD silently no-ops - the same trap the catalog loader
        // guards against, reproduced here in generated script form.
        var script = Emit(w => w.AppendInverse(1, UserTarget(value: "MouseSpeed"), new RegistryProbe
        {
            Presence = RegPresence.ValuePresent,
            Value = new RegistryData { Kind = "String", Data = JsonSerializer.SerializeToElement("1") },
        }));

        Assert.Contains("/t REG_SZ", script);
        Assert.Contains("/v \"MouseSpeed\"", script);
        Assert.Contains("\"1\"", script);
    }

    [Fact]
    public void Dword_is_emitted_as_hex_for_reg_exe()
    {
        var script = Emit(w => w.AppendInverse(1, UserTarget(), new RegistryProbe
        {
            Presence = RegPresence.ValuePresent,
            Value = EngineTestHarness.Dword(1),
        }));

        Assert.Contains("/t REG_DWORD", script);
        Assert.Contains("/d 0x1", script);
    }

    [Fact]
    public void Per_user_targets_use_HKU_and_the_captured_sid_not_HKCU()
    {
        // HKCU in an elevated .cmd resolves to whoever runs it. The script must name the hive it
        // actually captured, or "undo" writes into the wrong user's profile.
        var script = Emit(w => w.AppendInverse(1, UserTarget(), new RegistryProbe { Presence = RegPresence.ValueAbsent }));

        Assert.Contains($@"HKU\{EngineTestHarness.Sid}\SOFTWARE\Quiesce\T", script);
        Assert.DoesNotContain("HKCU", script);
    }

    [Fact]
    public void Keys_we_created_are_deleted_deepest_first()
    {
        var script = Emit(w => w.AppendInverse(1, UserTarget(@"SOFTWARE\Quiesce\A\B\C"), new RegistryProbe
        {
            Presence = RegPresence.KeyAbsent,
            MissingKeyPath = @"A\B\C",
        }));

        // Key-scoped deletes only (the value-scoped one carries /v).
        var keyDeletes = script.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("reg delete") && !l.Contains("/v "))
            .ToList();

        var deepest = keyDeletes.FindIndex(l => l.Contains(@"\A\B\C"));
        var shallowest = keyDeletes.FindIndex(l => l.Contains(@"\A""") || l.Contains(@"\A "));

        Assert.True(deepest >= 0, $"no delete for the deepest created key. Script:\n{script}");
        Assert.True(shallowest > deepest, $"created keys must be deleted deepest-first. Script:\n{script}");
    }

    [Fact]
    public void Key_that_already_existed_is_never_deleted()
    {
        // ValueAbsent means the key was there and only the value was missing. Deleting the key
        // would destroy sibling values Quiesce never touched.
        var script = Emit(w => w.AppendInverse(1, UserTarget(@"SOFTWARE\Microsoft\Windows"), new RegistryProbe
        {
            Presence = RegPresence.ValueAbsent,
        }));

        var deletes = script.Split('\n').Where(l => l.TrimStart().StartsWith("reg delete")).ToList();

        Assert.Single(deletes);
        Assert.Contains("/v ", deletes[0]); // value-scoped delete only
    }

    [Fact]
    public void Script_is_ascii_with_crlf_so_cmd_can_run_it()
    {
        Emit(w => w.AppendInverse(1, UserTarget(), new RegistryProbe { Presence = RegPresence.ValueAbsent }));

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "revert.cmd"));

        // A UTF-8 BOM makes cmd.exe fail on the first line.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "revert.cmd must not start with a UTF-8 BOM.");
        Assert.All(bytes, b => Assert.True(b < 0x80, "revert.cmd must be pure ASCII."));
        Assert.Contains("\r\n", File.ReadAllText(Path.Combine(_dir, "revert.cmd")));
    }

    [Fact]
    public void Activation_that_reg_exe_cannot_replay_is_called_out()
    {
        // Honesty in the fallback path too: the script says what it cannot do rather than
        // implying a complete undo.
        var script = Emit(w => w.AppendNote("step 3 also needs SpiSetMouse replayed; reg.exe cannot do that."));

        Assert.Contains("SpiSetMouse", script);
        Assert.Contains("REM", script);
    }
}
