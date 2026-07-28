using System.Diagnostics;

namespace Quiesce.Tests;

/// <summary>
/// End-to-end tests against the real <c>quiesce.exe</c>, using a temp data root and a temp catalog
/// so nothing on the developer's machine is touched.
/// </summary>
/// <remarks>
/// These exist because unit tests on the engine cannot catch a defect in the CLI wrapper around it.
/// The catalog-free revert test below is here for exactly that reason: the engine's revert never
/// reads a catalog, but an earlier version of the CLI resolved the catalog eagerly in shared setup,
/// so <c>revert-all</c> — the panic button — failed with the catalog missing while every unit test
/// still passed.
/// </remarks>
public class CliContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quiesce-cli-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Registry subkey unique to this test instance. The 7 tests in this class previously shared
    /// one key, which made them order-dependent and intermittently flaky.
    /// </summary>
    private readonly string _keySuffix = Guid.NewGuid().ToString("N")[..8];

    private string TestKeyPath => $@"SOFTWARE\Quiesce\CliTest\{_keySuffix}";

    public CliContractTests()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CatalogDir);

        // Entries are opt-in: a catalog row does nothing until a profile enables it, so that
        // shipping a new catalog can never silently start applying tweaks. The test opts in
        // exactly the way a user would.
        File.WriteAllText(Path.Combine(DataRoot, "profiles.json"), """
        {
          "schemaVersion": 1,
          "active": "default",
          "profiles": { "default": { "enabled": ["test.cli-roundtrip"] } }
        }
        """);

        // HKCU target under a Quiesce-only test key: writable unelevated, and never a real setting.
        File.WriteAllText(Path.Combine(CatalogDir, "tweaks.json"), """
        {
          "schemaVersion": 1,
          "catalogVersion": "cli-test",
          "entries": [
            {
              "id": "test.cli-roundtrip",
              "category": "test",
              "title": "CLI round-trip probe",
              "evidence": "Cosmetic",
              "impact": "None",
              "riskTier": 1,
              "scope": "Persistent",
              "requiresAdmin": false,
              "requiresReboot": false,
              "ops": [
                {
                  "kind": "registry",
                  "hive": "HKCU",
                  "view": "Registry64",
                  "subkey": "SOFTWARE\\Quiesce\\CliTest\\__SUFFIX__",
                  "value": "Probe",
                  "expectedKind": "DWord",
                  "leanData": 0
                }
              ],
              "whatItBreaks": "nothing (test-only key)"
            }
          ]
        }
        """.Replace("__SUFFIX__", _keySuffix));
    }

    private string DataRoot => Path.Combine(_root, "data");

    private string CatalogDir => Path.Combine(_root, "catalog");

    public void Dispose()
    {
        // Best-effort: revert anything left engaged, then remove the test registry key.
        try
        {
            Run("revert-all");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }

        try
        {
            using var hkcu = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
            hkcu.DeleteSubKeyTree(TestKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Asserts an exit code and, on failure, reports what the process actually printed.
    /// A bare exit-code assert on a subprocess gives you a number and no way to act on it.
    /// </summary>
    private static void AssertExit(int expected, (int ExitCode, string Stdout, string Stderr) result, string what)
    {
        if (result.ExitCode == expected)
        {
            return;
        }

        Assert.Fail($"""
            {what}: expected exit {expected}, got {result.ExitCode} (0x{result.ExitCode:X8}).
            --- stdout ---
            {result.Stdout}
            --- stderr ---
            {result.Stderr}
            """);
    }

    [Fact]
    public void Help_exits_zero_and_unknown_verb_exits_two()
    {
        AssertExit(0, Run("--help"), "--help");
        AssertExit(2, Run("definitely-not-a-verb"), "unknown verb");
    }

    [Fact]
    public void Print_plan_changes_nothing()
    {
        var result = Run("print-plan");

        AssertExit(0, result, "print-plan");
        Assert.Contains("Nothing has been changed", result.Stdout);
        Assert.Null(ReadProbe());
    }

    [Fact]
    public void Engage_then_restore_round_trips_the_real_registry()
    {
        Assert.Null(ReadProbe());

        AssertExit(0, Run("engage"), "engage");
        Assert.Equal(0, ReadProbe());

        AssertExit(0, Run("restore"), "restore");
        Assert.Null(ReadProbe()); // absent again, NOT zero
    }

    [Fact]
    public void Revert_all_works_with_the_catalog_deleted()
    {
        // The panic button must not depend on the catalog. Regression test for a real defect.
        AssertExit(0, Run("engage"), "engage");
        Assert.Equal(0, ReadProbe());

        Directory.Delete(CatalogDir, recursive: true);

        var result = Run("revert-all");

        AssertExit(0, result, "revert-all");
        Assert.Null(ReadProbe());
    }

    [Fact]
    public void Inventory_still_reports_dirty_state_with_no_catalog()
    {
        AssertExit(0, Run("engage"), "engage");
        Directory.Delete(CatalogDir, recursive: true);

        var result = Run("inventory");

        AssertExit(0, result, "inventory");
        Assert.Contains("ENGAGED", result.Stdout);
    }

    [Fact]
    public void Crash_mid_apply_leaves_state_recoverable()
    {
        var crash = Run("engage", "--fault-inject=afterStep1");

        // The process dies on the injected fault rather than exiting cleanly.
        Assert.NotEqual(0, crash.ExitCode);
        Assert.Equal(0, ReadProbe()); // half-applied

        AssertExit(0, Run("recover"), "recover");
        Assert.Null(ReadProbe()); // unwound to absent
    }

    [Fact]
    public void Verify_revert_reports_a_clean_round_trip()
    {
        var result = Run("verify-revert");

        AssertExit(0, result, "verify-revert");
        Assert.Contains("0 mismatch(es)", result.Stdout);
        Assert.Contains("dirty=False", result.Stdout);
    }

    // ---------------------------------------------------------------- helpers

    private int? ReadProbe()
    {
        using var hkcu = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
        using var key = hkcu.OpenSubKey(TestKeyPath);
        return key?.GetValue("Probe") as int?;
    }

    /// <summary>
    /// Finds quiesce.exe by walking up to the repo root (marked by global.json) rather than
    /// counting <c>..</c> segments, which silently breaks whenever the output path shape changes.
    /// </summary>
    private static string LocateCliExe()
    {
        // Runnable = apphost AND its managed assembly. The ProjectReference copies the apphost
        // quiesce.exe into this test's output without quiesce.dll, and that orphaned stub fails at
        // startup with "the application to execute does not exist" - so existence of the .exe alone
        // is not evidence that it can run.
        static bool IsRunnable(string exe) =>
            File.Exists(exe) && File.Exists(Path.ChangeExtension(exe, ".dll"));

        // Deliberately skip AppContext.BaseDirectory: referencing Quiesce.App drops ITS elevated
        // apphost into this directory, and on some SDK versions that overwrites quiesce.exe with a
        // requireAdministrator manifest. Launching that raises "the requested operation requires
        // elevation" instead of running the CLI. Always take the exe from the CLI's own bin.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                continue;
            }

            var cliBin = Path.Combine(dir.FullName, "src", "Quiesce.Cli", "bin");
            var found = Directory.Exists(cliBin)
                ? Directory.EnumerateFiles(cliBin, "quiesce.exe", SearchOption.AllDirectories).FirstOrDefault(IsRunnable)
                : null;

            return found ?? throw new FileNotFoundException($"No runnable quiesce.exe under {cliBin}. Build Quiesce.Cli first.");
        }

        throw new DirectoryNotFoundException($"Could not find the repo root (global.json) above {AppContext.BaseDirectory}.");
    }

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var exe = LocateCliExe();

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["QUIESCE_DATA_ROOT"] = DataRoot;
        psi.Environment["QUIESCE_CATALOG"] = Path.Combine(CatalogDir, "tweaks.json");

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        return (process.ExitCode, stdout, stderr);
    }
}
