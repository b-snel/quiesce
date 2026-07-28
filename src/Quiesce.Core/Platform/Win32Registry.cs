using Microsoft.Win32;

namespace Quiesce.Core.Platform;

/// <summary>The production <see cref="IRegistry"/>, backed by the real Windows registry.</summary>
/// <remarks>
/// Every open goes through <see cref="RegistryKey.OpenBaseKey"/> with an explicit
/// <see cref="RegistryView.Registry64"/>. The <c>Registry.LocalMachine</c>/<c>CurrentUser</c>
/// statics are banned (see <c>BannedSymbols.txt</c>): they use <c>RegistryView.Default</c>, which
/// on a non-x64 process redirects HKLM\SOFTWARE writes into WOW6432Node — a write that "succeeds"
/// and changes nothing.
/// </remarks>
public sealed class Win32Registry : IRegistry
{
    public RegistryProbe Probe(RegistryTarget target)
    {
        using var baseKey = OpenBase(target);
        using var key = baseKey.OpenSubKey(SubPath(target), writable: false);

        if (key is null)
        {
            return new RegistryProbe
            {
                Presence = RegPresence.KeyAbsent,
                MissingKeyPath = FindMissingPath(baseKey, SubPath(target)),
            };
        }

        // GetValueNames + contains, not GetValue with a sentinel default: a stored value could
        // legitimately equal any sentinel we might pick.
        var exists = key.GetValueNames().Contains(target.ValueName, StringComparer.OrdinalIgnoreCase);
        if (!exists)
        {
            return new RegistryProbe { Presence = RegPresence.ValueAbsent };
        }

        var kind = key.GetValueKind(target.ValueName);

        // DoNotExpandEnvironmentNames: an ExpandString read back expanded and then written back
        // would permanently bake in this machine's current %VARS%.
        var raw = key.GetValue(target.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new IOException($"{target}: value vanished between enumeration and read.");

        return new RegistryProbe
        {
            Presence = RegPresence.ValuePresent,
            Value = RegistryData.FromClrValue(kind, raw),
        };
    }

    public string? SetValue(RegistryTarget target, RegistryData data)
    {
        using var baseKey = OpenBase(target);

        // Capture what will be created BEFORE creating it, so the journal can record exactly
        // which keys are ours to delete on restore.
        var missing = FindMissingPath(baseKey, SubPath(target));

        using var key = baseKey.CreateSubKey(SubPath(target), writable: true)
            ?? throw new IOException($"{target}: CreateSubKey returned null.");

        key.SetValue(target.ValueName, data.ToClrValue(), data.ValueKind);
        return missing;
    }

    public void DeleteValue(RegistryTarget target)
    {
        using var baseKey = OpenBase(target);
        using var key = baseKey.OpenSubKey(SubPath(target), writable: true);

        // Key or value already gone is success, not failure: restore must be idempotent so that a
        // crash during recovery is survivable by running recovery again.
        key?.DeleteValue(target.ValueName, throwOnMissingValue: false);
    }

    public void DeleteCreatedKeysIfEmpty(RegistryTarget target, string relativeCreatedPath)
    {
        using var baseKey = OpenBase(target);

        var subPath = SubPath(target);
        var fullChain = subPath.Split('\\');
        var createdDepth = relativeCreatedPath.Split('\\').Length;

        // Delete deepest-first, stopping at the first key that is not empty: a key someone else
        // has since written into is no longer ours to remove.
        for (var depth = fullChain.Length; depth > fullChain.Length - createdDepth; depth--)
        {
            var path = string.Join('\\', fullChain.Take(depth));
            using var key = baseKey.OpenSubKey(path, writable: false);

            if (key is null)
            {
                continue; // already gone - idempotent
            }

            if (key.ValueCount > 0 || key.SubKeyCount > 0)
            {
                return;
            }

            baseKey.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }

    public bool UserHiveLoaded(string sid)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var hive = users.OpenSubKey(sid, writable: false);
        return hive is not null;
    }

    private static RegistryKey OpenBase(RegistryTarget target) => target.Hive switch
    {
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKU" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
        _ => throw new ArgumentException($"Unsupported hive '{target.Hive}'."),
    };

    private static string SubPath(RegistryTarget target) => target.Hive switch
    {
        "HKU" => $@"{target.UserSid ?? throw new ArgumentException("HKU target requires a userSid.")}\{target.Subkey}",
        _ => target.Subkey,
    };

    /// <summary>
    /// Walks down the subkey chain and returns the part that does not exist yet (relative to the
    /// deepest existing key), or null when the whole chain exists.
    /// </summary>
    private static string? FindMissingPath(RegistryKey baseKey, string subPath)
    {
        var parts = subPath.Split('\\');

        for (var depth = parts.Length; depth >= 1; depth--)
        {
            var candidate = string.Join('\\', parts.Take(depth));
            using var key = baseKey.OpenSubKey(candidate, writable: false);

            if (key is not null)
            {
                return depth == parts.Length ? null : string.Join('\\', parts.Skip(depth));
            }
        }

        return subPath;
    }
}
