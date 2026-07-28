using System.Text.Json;

namespace Quiesce.Core.Catalog;

/// <summary>
/// Catalog entries the user added themselves, stored beside the journal and merged with the shipped
/// catalog at load.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS SAFE TO TRUST. It lives in the data root, which is created with inheritance broken and
/// non-admin write removed, and which the ACL preflight already audits — the same protection that lets an
/// elevated Quiesce execute the revert plan it finds there. A file a standard user could rewrite would be
/// an arbitrary-close-as-Administrator primitive, so it is deliberately not stored anywhere the user's own
/// account can write.
/// </para>
/// <para>
/// It is also validated by <see cref="CatalogLoader"/> exactly as the shipped file is, which is the
/// second half of the argument: an entry written here cannot name a protected process, cannot mix a close
/// with anything reversible, cannot claim admin rights it does not need, and cannot carry an unanchored
/// directory fragment. Data narrows what Quiesce will touch; it never widens it, and a file the user
/// wrote is data like any other.
/// </para>
/// </remarks>
public sealed class UserCatalogStore(string dataRoot)
{
    public const string FileName = "user-apps.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path => System.IO.Path.Combine(dataRoot, FileName);

    /// <summary>
    /// Reads the user file, or null when there genuinely isn't one.
    /// </summary>
    /// <remarks>
    /// Opened rather than probed, for the reason <see cref="Journal.StateStore.Load"/> documents at
    /// length: <c>File.Exists</c> returns false for "not permitted to look", and this file lives in the
    /// Administrators-only data root. Treating a denial as "no user entries" would silently drop the
    /// user's own additions out of every plan while the UI went on showing them.
    /// </remarks>
    public CatalogFile? Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new Journal.StateUnreadableException(Path, ex);
        }

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return CatalogLoader.Load(stream, FileName);
    }

    public void Save(CatalogFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Validated before it reaches the disk as well as after it is read back. Writing a file this
        // build would then refuse to load would leave the user with a broken catalog and no way to see why.
        CatalogLoader.Validate(file, FileName);

        Directory.CreateDirectory(dataRoot);

        var tmp = Path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, file, WriteOptions);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(Path))
        {
            File.Replace(tmp, Path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, Path);
        }
    }

    /// <summary>Adds an entry for one discovered application and returns its id.</summary>
    public string Add(AppCandidate candidate, ProcessAction action, ThrottleLevel? throttleTo, CatalogFile? shipped)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var existing = Load();
        var taken = (shipped?.Entries.Select(e => e.Id) ?? [])
            .Concat(existing?.Entries.Select(e => e.Id) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entry = EntryFor(candidate, action, throttleTo, taken);

        Save(new CatalogFile
        {
            SchemaVersion = CatalogLoader.SupportedSchemaVersion,
            CatalogVersion = "user",
            Entries = [.. existing?.Entries ?? [], entry],
        });

        return entry.Id;
    }

    /// <summary>Removes a user entry. Shipped entries are not touchable this way and never should be.</summary>
    public bool Remove(string entryId)
    {
        var existing = Load();
        if (existing is null)
        {
            return false;
        }

        var kept = existing.Entries.Where(e => !e.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == existing.Entries.Count)
        {
            return false;
        }

        Save(existing with { Entries = kept });
        return true;
    }

    /// <summary>
    /// Builds the entry for a discovered application.
    /// </summary>
    /// <remarks>
    /// The op it produces is indistinguishable in kind from a shipped one — an image name plus the
    /// directory the application was actually found in. That is the point: discovery is a way of
    /// <em>authoring</em> a precise catalog entry, not a looser way of matching.
    /// </remarks>
    public static CatalogEntry EntryFor(
        AppCandidate candidate,
        ProcessAction action,
        ThrottleLevel? throttleTo,
        IReadOnlySet<string>? takenIds = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var verb = action == ProcessAction.Close ? "close" : "throttle";
        var id = UniqueId($"apps.user.{verb}-{Slug(candidate.DisplayName)}", takenIds);

        // One op per image name found in the directory. An Electron application runs its main process and
        // its helpers under different names out of one install tree, and naming only the one with the
        // window would leave the helpers running — which for the apps this is aimed at is most of the
        // memory and most of the timer-resolution abuse.
        var ops = candidate.ImageNames.Select(name => (OpSpec)new ProcessOpSpec
        {
            Action = action,
            ImageName = name,
            UnderDirectories = [candidate.DirectoryFragment],
            ThrottleTo = action == ProcessAction.Throttle ? throttleTo ?? ThrottleLevel.BelowNormal : null,
        }).ToList();

        var breaks = action == ProcessAction.Close
            ? $"{candidate.DisplayName} is closed, with the same save-your-work prompt you would get from " +
              "the window's X button. Restore does NOT reopen it — closing is the one thing Quiesce does " +
              "that its undo does not cover."
            : $"{candidate.DisplayName} keeps running but gets CPU only when nothing else wants it. " +
              "Restore puts its priority back exactly.";

        return new CatalogEntry
        {
            Id = id,
            Category = "apps",
            Title = action == ProcessAction.Close
                ? $"Close {candidate.DisplayName}"
                : $"Throttle {candidate.DisplayName}",
            // Situational, not Measured, and this is not modesty. Nothing has been measured about this
            // application: the user asserted that closing it helps them, which is a different claim, and
            // the evidence field exists so that difference stays visible.
            Evidence = Evidence.Situational,
            Impact = Impact.Medium,
            RiskTier = 1,
            Scope = TweakScope.Session,
            RequiresAdmin = false,
            RequiresReboot = false,
            Ops = ops,
            WhatItBreaks = breaks,
            Notes = $"Added from the running-apps list. Matched by image name under " +
                    $"{candidate.DirectoryFragment} — the same path-based rule as every shipped entry, so " +
                    $"a copy of {candidate.DisplayName} anywhere else on disk is not touched. Seen running " +
                    $"as {candidate.ProcessCount} process(es) when it was added.",
        };
    }

    /// <summary>
    /// Merges user entries into the shipped catalog, refusing the whole merge if the result is invalid.
    /// </summary>
    /// <remarks>
    /// The combined version string carries both halves, because it is written into the journal and a
    /// journal that says <c>0.5.0</c> about a session that also applied user entries cannot be read back
    /// accurately during a recovery.
    /// </remarks>
    public static CatalogFile Merge(CatalogFile shipped, CatalogFile? user)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        if (user is null || user.Entries.Count == 0)
        {
            return shipped;
        }

        var merged = shipped with
        {
            CatalogVersion = $"{shipped.CatalogVersion}+user.{user.Entries.Count}",
            Entries = [.. shipped.Entries, .. user.Entries],
        };

        // Re-validated as a whole, which is what catches an id colliding with a shipped entry — neither
        // file can see the other on its own, and a duplicate id would make the profile ambiguous about
        // which entry it had enabled.
        CatalogLoader.Validate(merged, $"{FileName} merged with the shipped catalog");
        return merged;
    }

    private static string UniqueId(string preferred, IReadOnlySet<string>? taken)
    {
        if (taken is null || !taken.Contains(preferred))
        {
            return preferred;
        }

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{preferred}-{n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new CatalogException($"Cannot find a free id for '{preferred}'.");
    }

    private static string Slug(string name)
    {
        var chars = name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray();
        return chars.Length == 0 ? "app" : new string(chars).ToLowerInvariant();
    }
}
