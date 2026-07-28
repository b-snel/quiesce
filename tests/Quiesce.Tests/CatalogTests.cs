using System.Text;
using Quiesce.Core;
using Quiesce.Core.Catalog;

namespace Quiesce.Tests;

/// <summary>
/// Catalog validation — including that the SHIPPED catalog file passes. This suite is the shape
/// gate CI relies on: validating against the exact types the engine deserializes to is stronger
/// than a parallel JSON Schema that can drift.
/// </summary>
public class CatalogTests
{
    [Fact]
    public void The_shipped_catalog_loads_and_validates()
    {
        var path = FindShippedCatalog();
        var catalog = CatalogLoader.LoadFile(path);

        Assert.NotEmpty(catalog.Entries);
    }

    [Fact]
    public void Future_schemaVersion_is_refused()
    {
        var json = """{ "schemaVersion": 99, "catalogVersion": "x", "entries": [] }""";

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("schemaVersion 99", ex.Message);
    }

    [Fact]
    public void Kind_mismatched_leanData_is_refused()
    {
        // A DWord write to a REG_SZ target silently no-ops; the mismatch must die at load time.
        var json = EntryJson(expectedKind: "String", leanData: "0");

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("does not match expectedKind", ex.Message);
    }

    [Fact]
    public void Unknown_op_kind_is_refused()
    {
        // "service" is a real kind now, so this uses a discriminator that genuinely does not exist.
        var json = EntryJson(opKind: "wmi");

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("malformed catalog", ex.Message);
    }

    [Fact]
    public void A_service_op_naming_a_protected_service_is_refused()
    {
        // Guardrails are compile-time constants that data can narrow but never widen. A catalog
        // shipped by anyone must not be able to talk Quiesce into reconfiguring a tier-0 service.
        var json = """
        {
          "schemaVersion": 1,
          "catalogVersion": "x",
          "entries": [{
            "id": "svc.bad", "category": "test", "title": "t",
            "evidence": "Measured", "impact": "Low", "riskTier": 1, "scope": "Session",
            "requiresAdmin": true, "requiresReboot": false,
            "ops": [{ "kind": "service", "service": "DcomLaunch", "startMode": "Disabled" }],
            "whatItBreaks": "everything"
          }]
        }
        """;

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("never-touch", ex.Message);
    }

    [Fact]
    public void A_service_op_must_declare_requiresAdmin()
    {
        var json = """
        {
          "schemaVersion": 1,
          "catalogVersion": "x",
          "entries": [{
            "id": "svc.noadmin", "category": "test", "title": "t",
            "evidence": "Measured", "impact": "Low", "riskTier": 1, "scope": "Session",
            "requiresAdmin": false, "requiresReboot": false,
            "ops": [{ "kind": "service", "service": "SysMain", "startMode": "Manual" }],
            "whatItBreaks": "nothing"
          }]
        }
        """;

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("requiresAdmin", ex.Message);
    }

    [Fact]
    public void Non_Registry64_view_is_refused()
    {
        var json = EntryJson(view: "Registry32");

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("Registry64", ex.Message);
    }

    [Fact]
    public void Duplicate_entry_ids_are_refused()
    {
        var entry = EntryFragment();
        var json = $$"""{ "schemaVersion": 1, "catalogVersion": "x", "entries": [{{entry}}, {{entry}}] }""";

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("duplicate id", ex.Message);
    }

    [Fact]
    public void Empty_whatItBreaks_is_refused()
    {
        // "Nothing" must be said explicitly - honesty is a schema requirement, not a convention.
        var json = EntryJson(whatItBreaks: "");

        var ex = Assert.Throws<CatalogException>(() => Load(json));
        Assert.Contains("whatItBreaks", ex.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static CatalogFile Load(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CatalogLoader.Load(stream, "test.json");
    }

    private static string EntryJson(
        string opKind = "registry",
        string view = "Registry64",
        string expectedKind = "DWord",
        string leanData = "0",
        string whatItBreaks = "nothing")
        => $$"""
        {
          "schemaVersion": 1,
          "catalogVersion": "x",
          "entries": [{{EntryFragment(opKind, view, expectedKind, leanData, whatItBreaks)}}]
        }
        """;

    private static string EntryFragment(
        string opKind = "registry",
        string view = "Registry64",
        string expectedKind = "DWord",
        string leanData = "0",
        string whatItBreaks = "nothing")
        => $$"""
        {
          "id": "test.entry",
          "category": "test",
          "title": "t",
          "evidence": "Measured",
          "impact": "Low",
          "riskTier": 1,
          "scope": "Persistent",
          "requiresAdmin": false,
          "requiresReboot": false,
          "ops": [{
            "kind": "{{opKind}}",
            "hive": "HKCU",
            "view": "{{view}}",
            "subkey": "SOFTWARE\\Test",
            "value": "V",
            "expectedKind": "{{expectedKind}}",
            "leanData": {{leanData}}
          }],
          "whatItBreaks": "{{whatItBreaks}}"
        }
        """;

    // ------------------------------------------------- process ops (M5)

    /// <summary>
    /// A one-op process entry, valid unless a parameter is deliberately spoiled.
    /// </summary>
    private static string ProcessEntryJson(
        string action = "close",
        string imageName = "chrome",
        string directories = @"[""\\Google\\Chrome\\Application\\""]",
        string extraOpFields = "",
        string scope = "Session",
        string requiresAdmin = "false",
        string extraOps = "")
        => $$"""
        {
          "id": "apps.test",
          "category": "apps",
          "title": "t",
          "evidence": "Situational",
          "impact": "Medium",
          "riskTier": 2,
          "scope": "{{scope}}",
          "requiresAdmin": {{requiresAdmin}},
          "requiresReboot": false,
          "ops": [{
            "kind": "process",
            "action": "{{action}}",
            "imageName": "{{imageName}}",
            "underDirectories": {{directories}}{{extraOpFields}}
          }{{extraOps}}],
          "whatItBreaks": "nothing"
        }
        """;

    private static CatalogException Refused(string entryJson) => Assert.Throws<CatalogException>(
        () => CatalogLoader.Load(
            new MemoryStream(Encoding.UTF8.GetBytes(
                $$"""{ "schemaVersion": 1, "catalogVersion": "x", "entries": [{{entryJson}}] }""")),
            "test.json"));

    [Fact]
    public void A_valid_process_entry_loads()
    {
        var file = CatalogLoader.Load(
            new MemoryStream(Encoding.UTF8.GetBytes(
                $$"""{ "schemaVersion": 1, "catalogVersion": "x", "entries": [{{ProcessEntryJson()}}] }""")),
            "test.json");

        var op = Assert.IsType<ProcessOpSpec>(Assert.Single(file.Entries).Ops[0]);
        Assert.Equal(ProcessAction.Close, op.Action);
        Assert.False(op.NeedsAdmin);
    }

    /// <summary>
    /// A drive-rooted fragment is accepted alongside the drive-relative form the shipped catalog uses.
    /// </summary>
    /// <remarks>
    /// It is the stricter of the two, not a relaxation: anchored at the root, it cannot match the same
    /// directory name appearing deeper in an unrelated path. The shipped catalog stays drive-relative
    /// because an install can be on any drive; entries written from the running-apps list are rooted,
    /// because discovery knows exactly where the application is.
    /// </remarks>
    [Fact]
    public void A_drive_rooted_directory_fragment_is_accepted()
    {
        var file = CatalogLoader.Load(
            new MemoryStream(Encoding.UTF8.GetBytes(
                $$"""
                { "schemaVersion": 1, "catalogVersion": "x", "entries": [
                  {{ProcessEntryJson(directories: @"[""C:\\Program Files\\Thing\\""]")}}
                ] }
                """)),
            "test.json");

        var op = Assert.IsType<ProcessOpSpec>(Assert.Single(file.Entries).Ops[0]);
        Assert.Equal([@"C:\Program Files\Thing\"], op.UnderDirectories);
    }

    /// <summary>
    /// A UNC fragment is refused. Matching is a substring test, and <c>\\server\share\...</c> has no
    /// anchor that test can rely on — the share prefix makes it look rooted while the part that matters
    /// begins mid-path.
    /// </summary>
    [Fact]
    public void A_unc_directory_fragment_is_refused() =>
        Assert.Contains(
            "backslash",
            Refused(ProcessEntryJson(directories: @"[""\\\\server\\share\\App\\""]")).Message);

    [Fact]
    public void A_catalog_cannot_name_a_never_touch_process()
    {
        // The rule that makes every guardrail meaningful: data narrows what Quiesce will touch and can
        // never widen it. A catalog shipped by anyone - including a future me - must not be able to talk
        // the app into closing the shell.
        Assert.Contains("never-touch", Refused(ProcessEntryJson(imageName: "explorer")).Message);
        Assert.Contains("never-touch", Refused(ProcessEntryJson(imageName: "csrss")).Message);

        // Hosts other applications' windows - Widgets, new Outlook, launcher panes. Closing it as if it
        // were Edge takes those with it, which is why it is never-touch rather than a browser.
        Assert.Contains("never-touch", Refused(ProcessEntryJson(imageName: "msedgewebview2")).Message);
    }

    [Fact]
    public void A_process_op_must_say_where_the_program_lives()
    {
        Assert.Contains("path-based", Refused(ProcessEntryJson(directories: "[]")).Message);
    }

    [Fact]
    public void A_directory_fragment_must_name_a_directory()
    {
        // Delimited at both ends, so it cannot match a longer name that merely starts the same way -
        // "\Discord" would otherwise also collect Discord Canary.
        Assert.Contains("backslash", Refused(ProcessEntryJson(directories: @"[""\\Discord""]")).Message);
        Assert.Contains("backslash", Refused(ProcessEntryJson(directories: @"[""Discord\\""]")).Message);
        Assert.Contains("every path", Refused(ProcessEntryJson(directories: @"[""\\""]")).Message);
        Assert.Contains("literal path fragment", Refused(ProcessEntryJson(directories: @"[""\\Apps\\*\\""]")).Message);
        Assert.Contains("literal path fragment", Refused(ProcessEntryJson(directories: @"[""\\Apps\\..\\""]")).Message);
    }

    [Fact]
    public void An_image_name_must_be_a_bare_name()
    {
        Assert.Contains("bare image name", Refused(ProcessEntryJson(imageName: @"C:\\Apps\\a")).Message);
    }

    [Fact]
    public void A_throttle_must_say_what_to_throttle_to_and_a_close_must_not()
    {
        Assert.Contains("what to throttle to", Refused(ProcessEntryJson(action: "throttle")).Message);
        Assert.Contains(
            "sets no priority",
            Refused(ProcessEntryJson(extraOpFields: ", \"throttleTo\": \"Idle\"")).Message);
    }

    [Fact]
    public void The_throttle_level_has_no_spelling_for_a_raise()
    {
        // The ceiling as a type rather than a check: there is no "High" in ThrottleLevel, so a catalog
        // asking for one does not fail validation - it fails to parse.
        var refused = Refused(ProcessEntryJson(action: "throttle", extraOpFields: ", \"throttleTo\": \"High\""));
        Assert.Contains("malformed catalog", refused.Message);
    }

    [Fact]
    public void An_entry_cannot_mix_a_close_with_anything_reversible()
    {
        // Entry-level atomicity is the transaction unit, and nothing unwinds "the application exited". An
        // entry that mixed the two could not honour "never half-applied", and the guarantee is worth more
        // than the flexibility.
        const string registryOp = """
        ,{
          "kind": "registry", "hive": "HKCU", "view": "Registry64",
          "subkey": "SOFTWARE\\Test", "value": "V", "expectedKind": "DWord", "leanData": 0
        }
        """;

        Assert.Contains("only process ops", Refused(ProcessEntryJson(extraOps: registryOp)).Message);
    }

    [Fact]
    public void An_entry_cannot_mix_close_and_throttle()
    {
        const string throttleOp = """
        ,{
          "kind": "process", "action": "throttle", "imageName": "firefox",
          "underDirectories": ["\\Mozilla Firefox\\"], "throttleTo": "Idle"
        }
        """;

        Assert.Contains("close and throttle", Refused(ProcessEntryJson(extraOps: throttleOp)).Message);
    }

    [Fact]
    public void Process_entries_must_be_session_scoped_and_must_not_claim_admin()
    {
        Assert.Contains("Session scope", Refused(ProcessEntryJson(scope: "Persistent")).Message);
        Assert.Contains("needs no elevation", Refused(ProcessEntryJson(requiresAdmin: "true")).Message);
    }

    [Fact]
    public void The_shipped_browser_group_does_not_target_the_webview_host()
    {
        // Six live instances on the development machine, hosting Widgets and new Outlook among others.
        // Closing it as if it were Edge takes those applications' windows with it.
        var catalog = CatalogLoader.LoadFile(FindShippedCatalog());

        var targeted = catalog.Entries
            .SelectMany(e => e.Ops.OfType<ProcessOpSpec>())
            .Select(op => op.ImageName)
            .ToList();

        Assert.NotEmpty(targeted);
        Assert.DoesNotContain("msedgewebview2", targeted, StringComparer.OrdinalIgnoreCase);
        Assert.All(targeted, name => Assert.False(Guardrails.IsProcessProtected(name)));
    }

    [Fact]
    public void Everything_the_default_profile_enables_exists_in_the_shipped_catalog()
    {
        // A default profile naming an entry the catalog does not have would silently enable nothing, and
        // the browser group was added to both in one change - exactly when a typo is easiest to make.
        var catalog = CatalogLoader.LoadFile(FindShippedCatalog());
        var ids = catalog.Entries.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(ProfileStore.BuiltInDefault, id => Assert.Contains(id, ids));
    }

    private static string FindShippedCatalog()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "catalog", "tweaks.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("catalog/tweaks.json not found above test output directory.");
    }
}
