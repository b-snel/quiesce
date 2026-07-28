using System.Text;
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
