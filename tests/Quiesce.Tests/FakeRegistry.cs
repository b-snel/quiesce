using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// In-memory <see cref="IRegistry"/> with real key/value semantics — including the ones the
/// engine's correctness depends on: value-absent vs key-absent, key creation tracking, and
/// "don't delete a created key someone else has since used".
/// </summary>
/// <remarks>
/// A stateful fake, not an NSubstitute mock: round-trip tests need writes to be visible to later
/// reads, which is behaviour, not stubbing.
/// </remarks>
public sealed class FakeRegistry : IRegistry
{
    private sealed class Key
    {
        public Dictionary<string, Key> SubKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RegistryData> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, Key> _roots = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _loadedHives = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Log { get; } = [];

    public FakeRegistry()
    {
        _roots["HKLM"] = new Key();
        _roots["HKU"] = new Key();
    }

    /// <summary>Marks a user hive as loaded (creating its root key). Tests control this explicitly.</summary>
    public void LoadUserHive(string sid)
    {
        _loadedHives.Add(sid);
        GetOrCreate(_roots["HKU"], sid);
    }

    public void UnloadUserHive(string sid) => _loadedHives.Remove(sid);

    public bool UserHiveLoaded(string sid) => _loadedHives.Contains(sid);

    /// <summary>Seeds a value directly, for arranging test state.</summary>
    public void Seed(RegistryTarget target, RegistryData data)
    {
        var key = GetOrCreate(Root(target), FullPath(target));
        key.Values[target.ValueName] = data;
    }

    /// <summary>Reads a value directly, for asserting test state. Null = absent.</summary>
    public RegistryData? Peek(RegistryTarget target)
    {
        var key = Find(Root(target), FullPath(target));
        return key is not null && key.Values.TryGetValue(target.ValueName, out var data) ? data : null;
    }

    public bool KeyExists(RegistryTarget target) => Find(Root(target), FullPath(target)) is not null;

    // ------------------------------------------------------------ IRegistry

    public RegistryProbe Probe(RegistryTarget target)
    {
        var key = Find(Root(target), FullPath(target));
        if (key is null)
        {
            return new RegistryProbe
            {
                Presence = RegPresence.KeyAbsent,
                MissingKeyPath = MissingPath(Root(target), FullPath(target)),
            };
        }

        return key.Values.TryGetValue(target.ValueName, out var data)
            ? new RegistryProbe { Presence = RegPresence.ValuePresent, Value = data }
            : new RegistryProbe { Presence = RegPresence.ValueAbsent };
    }

    /// <summary>
    /// Value targets a kernel registry callback refuses to let anyone write OR delete, even with a
    /// permissive DACL, an elevated caller, and an operation that would change nothing.
    /// </summary>
    /// <remarks>
    /// Models real observed behaviour: on Windows 11,
    /// <c>HKLM\SOFTWARE\Policies\Microsoft\Dsh!AllowNewsAndInterests</c> is vetoed on that exact
    /// (key, value name) pair while the same key accepts every other name and the same name is
    /// accepted in other keys. Crucially the veto covers the DELETE too, which is what turned a
    /// failed apply into a session that could never be reverted.
    /// </remarks>
    public void VetoWritesTo(RegistryTarget target) => _vetoed.Add(VetoKey(target));

    private readonly HashSet<string> _vetoed = new(StringComparer.OrdinalIgnoreCase);

    private static string VetoKey(RegistryTarget t) => $"{t.Hive}|{t.UserSid}|{t.Subkey}|{t.ValueName}";

    private void ThrowIfVetoed(RegistryTarget target)
    {
        if (_vetoed.Contains(VetoKey(target)))
        {
            throw new UnauthorizedAccessException("Attempted to perform an unauthorized operation.");
        }
    }

    public string? SetValue(RegistryTarget target, RegistryData data)
    {
        ThrowIfVetoed(target);
        Log.Add($"set {target} = {data.Kind}");
        var missing = MissingPath(Root(target), FullPath(target));
        var key = GetOrCreate(Root(target), FullPath(target));
        key.Values[target.ValueName] = data;
        return missing;
    }

    public void DeleteValue(RegistryTarget target)
    {
        ThrowIfVetoed(target);
        Log.Add($"del {target}");
        Find(Root(target), FullPath(target))?.Values.Remove(target.ValueName);
    }

    public void DeleteCreatedKeysIfEmpty(RegistryTarget target, string relativeCreatedPath)
    {
        var full = FullPath(target).Split('\\');
        var createdDepth = relativeCreatedPath.Split('\\').Length;

        for (var depth = full.Length; depth > full.Length - createdDepth; depth--)
        {
            var path = string.Join('\\', full.Take(depth));
            var key = Find(Root(target), path);
            if (key is null)
            {
                continue;
            }

            if (key.Values.Count > 0 || key.SubKeys.Count > 0)
            {
                return; // someone else uses it now - not ours to delete
            }

            var parentPath = string.Join('\\', full.Take(depth - 1));
            var parent = depth == 1 ? Root(target) : Find(Root(target), parentPath);
            parent?.SubKeys.Remove(full[depth - 1]);
            Log.Add($"delkey {target.Hive}\\{path}");
        }
    }

    // -------------------------------------------------------------- helpers

    private Key Root(RegistryTarget target) => _roots[target.Hive];

    private static string FullPath(RegistryTarget target) =>
        target.Hive == "HKU" ? $@"{target.UserSid}\{target.Subkey}" : target.Subkey;

    private static Key? Find(Key root, string path)
    {
        var current = root;
        foreach (var part in path.Split('\\'))
        {
            if (!current.SubKeys.TryGetValue(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static Key GetOrCreate(Key root, string path)
    {
        var current = root;
        foreach (var part in path.Split('\\'))
        {
            if (!current.SubKeys.TryGetValue(part, out var next))
            {
                next = new Key();
                current.SubKeys[part] = next;
            }

            current = next;
        }

        return current;
    }

    private static string? MissingPath(Key root, string path)
    {
        var parts = path.Split('\\');
        var current = root;

        for (var i = 0; i < parts.Length; i++)
        {
            if (!current.SubKeys.TryGetValue(parts[i], out current!))
            {
                return string.Join('\\', parts.Skip(i));
            }
        }

        return null;
    }
}

/// <summary>Records broadcasts so tests can assert revert re-issues them.</summary>
public sealed class FakeBroadcaster : IActivationBroadcaster
{
    public List<ActivationKind> Broadcasts { get; } = [];

    public void Broadcast(ActivationKind kind) => Broadcasts.Add(kind);
}
