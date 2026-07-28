namespace Quiesce.Core.Catalog;

/// <summary>Shared catalog resolution for the CLI and the GUI.</summary>
public static class CatalogLocator
{
    public const string CatalogEnvVar = "QUIESCE_CATALOG";

    /// <summary>
    /// Resolution order: env override, then walking up from the executable looking for
    /// <c>catalog\tweaks.json</c> — which covers both the installed layout (catalog next to the
    /// exe) and the dev layout (exe deep in bin\...).
    /// Returns null rather than throwing: absence is legitimate for the revert paths, which read
    /// only the journal.
    /// </summary>
    public static string? TryLocate(string imagePath)
    {
        if (Environment.GetEnvironmentVariable(CatalogEnvVar) is { Length: > 0 } overridePath)
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        var dir = Path.GetDirectoryName(imagePath)!;

        for (var probe = new DirectoryInfo(dir); probe is not null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "catalog", "tweaks.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
