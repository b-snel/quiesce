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
/// <summary>What <see cref="UserCatalogStore.Add"/> did.</summary>
public enum UserEntryOutcome
{
    /// <summary>A new entry was written.</summary>
    Added,

    /// <summary>An entry already covered this directory and action; executables were added to it.</summary>
    Extended,

    /// <summary>An entry already covered this directory and action completely. Nothing was written.</summary>
    AlreadyPresent,
}

/// <summary>The result of adding a discovered application, said precisely enough to report to the user.</summary>
public sealed record UserEntryResult
{
    public required string EntryId { get; init; }

    public required UserEntryOutcome Outcome { get; init; }

    /// <summary>Executables this call brought under the entry. Empty when nothing changed.</summary>
    public required IReadOnlyList<string> AddedImageNames { get; init; }
}

/// <summary>
/// What one gesture on the merged page authored. Null means "already covered; nothing written".
/// </summary>
/// <remarks>
/// Two ids rather than one, deliberately visible to the caller, because they are two different promises: the
/// close is <c>Session</c>-scoped and Restore does NOT reopen what it closed, while the sign-in preference is
/// <c>Persistent</c> and Restore puts it back exactly. A result type that hid the distinction would invite a
/// confirmation message that made one of those two sentences false.
/// </remarks>
public sealed record CombinedEntryResult
{
    public required string? CloseEntryId { get; init; }

    public required string? StartupEntryId { get; init; }

    /// <summary>Every id this call wrote, for enabling them in the profile.</summary>
    public IReadOnlyList<string> WrittenIds =>
        [.. new[] { CloseEntryId, StartupEntryId }.Where(id => id is not null).Select(id => id!)];

    public bool WroteNothing => CloseEntryId is null && StartupEntryId is null;
}

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

    /// <summary>
    /// Adds — or extends — the entry for one discovered application.
    /// </summary>
    /// <remarks>
    /// AN UPSERT, NOT AN APPEND, and that distinction was learned the hard way: pressing Throttle four
    /// times produced <c>apps.user.throttle-applephotostreams</c>, <c>-2</c>, <c>-3</c> and <c>-4</c>, four
    /// entries doing the same thing to the same directory. The id-disambiguation suffix exists for two
    /// <em>different</em> applications that happen to share a display name, and using it for the same
    /// application twice turned a duplicate into four indistinguishable rows in Features.
    /// <para>
    /// Identity is (directory, action). The image names are not part of it: the set of executables running
    /// out of one install tree changes between scans as helpers come and go, so the same application
    /// legitimately presents a different name list each time. When a rescan finds names the stored entry
    /// does not cover, the entry is extended rather than duplicated — which is also the only way an entry
    /// added while three helpers were running ever comes to cover the other three.
    /// </para>
    /// </remarks>
    public UserEntryResult Add(AppCandidate candidate, ProcessAction action, ThrottleLevel? throttleTo, CatalogFile? shipped)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var existing = Load();

        if (FindEquivalent(existing, candidate, action) is { } match)
        {
            var covered = match.Ops.OfType<ProcessOpSpec>()
                .Select(op => ProcessOpSpec.Bare(op.ImageName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = candidate.ImageNames
                .Where(name => !covered.Contains(ProcessOpSpec.Bare(name)))
                .ToList();

            if (missing.Count == 0)
            {
                return new UserEntryResult
                {
                    EntryId = match.Id,
                    Outcome = UserEntryOutcome.AlreadyPresent,
                    AddedImageNames = [],
                };
            }

            var extended = match with
            {
                Ops =
                [
                    .. match.Ops,
                    .. missing.Select(name => (OpSpec)new ProcessOpSpec
                    {
                        Action = action,
                        ImageName = name,
                        UnderDirectories = [candidate.DirectoryFragment],
                        ThrottleTo = action == ProcessAction.Throttle ? throttleTo ?? ThrottleLevel.BelowNormal : null,
                    }),
                ],
            };

            Save(existing! with
            {
                Entries = [.. existing.Entries.Select(e => e.Id == match.Id ? extended : e)],
            });

            return new UserEntryResult
            {
                EntryId = match.Id,
                Outcome = UserEntryOutcome.Extended,
                AddedImageNames = missing,
            };
        }

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

        return new UserEntryResult
        {
            EntryId = entry.Id,
            Outcome = UserEntryOutcome.Added,
            AddedImageNames = candidate.ImageNames,
        };
    }

    /// <summary>
    /// The stored entry that already does <paramref name="action"/> to this candidate's directory, if any.
    /// </summary>
    private static CatalogEntry? FindEquivalent(CatalogFile? existing, AppCandidate candidate, ProcessAction action) =>
        existing?.Entries.FirstOrDefault(entry =>
            entry.Ops.OfType<ProcessOpSpec>().Any(op =>
                op.Action == action
                && op.UnderDirectories.Any(d =>
                    d.Equals(candidate.DirectoryFragment, StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Adds — or refreshes — the entry that stops one discovered item running at sign-in.
    /// </summary>
    /// <remarks>
    /// Keyed on the exact (hive, subkey, value) the op writes, which is a stronger identity than the
    /// running-app case has: an approval value names one entry at one location and nothing else can
    /// collide with it. Refreshes rather than duplicating for the reason the app flow learned the hard way,
    /// and refreshing is not cosmetic here — the lean bytes are derived from the blob observed at
    /// authoring time, so re-adding after the entry was toggled by hand rewrites them to match.
    /// </remarks>
    /// <summary>
    /// Authors the close entry and the sign-in entry in ONE validated write. Both, or neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>Save</c>, not two calls. <see cref="Add"/> and <see cref="AddStartupDisable"/> each do their own
    /// Load-then-Save, so a two-call gesture can leave the close entry durably written while reporting
    /// failure — and the half-written case is reachable, not theoretical:
    /// <c>StartupItemDiscovery.EntryFor</c> throws for a logon task, and <c>CatalogLoader.ValidateRegistry</c>
    /// refuses an empty value name, which an HKCU <c>Run</c> DEFAULT value produces and HKCU is
    /// standard-user-writable.
    /// </para>
    /// <para>
    /// Composed from the two single-purpose methods rather than reimplementing either: the extend-rather-than-
    /// duplicate behaviour they carry is what fixed the four-presses-four-entries bug, and a second write path
    /// that did not share it would reintroduce it. They are called against a snapshot and the result is
    /// written once, which is what makes the atomicity real rather than nominal.
    /// </para>
    /// <para>
    /// TWO ENTRIES, NOT ONE, and the caller must say so: a close is <c>Session</c>-scoped and has no undo,
    /// while a sign-in preference is <c>Persistent</c> and Restore puts it back exactly. Presenting them as
    /// one thing would make one of those two sentences false.
    /// </para>
    /// </remarks>
    public CombinedEntryResult AddAppAndStartup(
        AppCandidate? candidate,
        ProcessAction action,
        ThrottleLevel? throttleTo,
        Startup.StartupItem? startupItem,
        CatalogFile? shipped)
    {
        if (candidate is null && startupItem is null)
        {
            throw new ArgumentException("Nothing to author: both the application and the sign-in entry are null.");
        }

        // Validated and composed BEFORE anything is written. Every refusal below happens with the file
        // untouched, which is the whole point of the method.
        var beforeAny = Load();
        var taken = (shipped?.Entries.Select(e => e.Id) ?? [])
            .Concat(beforeAny?.Entries.Select(e => e.Id) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CatalogEntry? closeEntry = null;
        CatalogEntry? startupEntry = null;

        if (candidate is not null && FindEquivalent(beforeAny, candidate, action) is null)
        {
            closeEntry = EntryFor(candidate, action, throttleTo, taken);
            taken.Add(closeEntry.Id);
        }

        if (startupItem is not null && FindByRegistryTarget(
                beforeAny,
                (RegistryOpSpec)Startup.StartupItemDiscovery.EntryFor(startupItem).Ops[0]) is null)
        {
            // Throws for a logon task, and it throws HERE - before the write - which is the reachable
            // half-written case this method exists to close.
            startupEntry = Startup.StartupItemDiscovery.EntryFor(startupItem, taken);
        }

        if (closeEntry is null && startupEntry is null)
        {
            return new CombinedEntryResult { CloseEntryId = null, StartupEntryId = null };
        }

        Save(new CatalogFile
        {
            SchemaVersion = CatalogLoader.SupportedSchemaVersion,
            CatalogVersion = "user",
            Entries =
            [
                .. beforeAny?.Entries ?? [],
                .. closeEntry is null ? Array.Empty<CatalogEntry>() : [closeEntry],
                .. startupEntry is null ? Array.Empty<CatalogEntry>() : [startupEntry],
            ],
        });

        return new CombinedEntryResult
        {
            CloseEntryId = closeEntry?.Id,
            StartupEntryId = startupEntry?.Id,
        };
    }

    public UserEntryResult AddStartupDisable(Startup.StartupItem item, CatalogFile? shipped)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existing = Load();
        var fresh = Startup.StartupItemDiscovery.EntryFor(item);
        var op = (RegistryOpSpec)fresh.Ops[0];

        if (FindByRegistryTarget(existing, op) is { } match)
        {
            var current = (RegistryOpSpec)match.Ops[0];

            if (current.LeanData.GetString() == op.LeanData.GetString())
            {
                return new UserEntryResult
                {
                    EntryId = match.Id,
                    Outcome = UserEntryOutcome.AlreadyPresent,
                    AddedImageNames = [],
                };
            }

            Save(existing! with
            {
                Entries = [.. existing.Entries.Select(e => e.Id == match.Id ? match with { Ops = fresh.Ops } : e)],
            });

            return new UserEntryResult
            {
                EntryId = match.Id,
                Outcome = UserEntryOutcome.Extended,
                AddedImageNames = [item.Name],
            };
        }

        var taken = (shipped?.Entries.Select(e => e.Id) ?? [])
            .Concat(existing?.Entries.Select(e => e.Id) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entry = Startup.StartupItemDiscovery.EntryFor(item, taken);

        Save(new CatalogFile
        {
            SchemaVersion = CatalogLoader.SupportedSchemaVersion,
            CatalogVersion = "user",
            Entries = [.. existing?.Entries ?? [], entry],
        });

        return new UserEntryResult
        {
            EntryId = entry.Id,
            Outcome = UserEntryOutcome.Added,
            AddedImageNames = [item.Name],
        };
    }

    /// <summary>The stored entry writing this exact registry target, if any.</summary>
    private static CatalogEntry? FindByRegistryTarget(CatalogFile? existing, RegistryOpSpec op) =>
        existing?.Entries.FirstOrDefault(entry =>
            entry.Ops.Count == 1
            && entry.Ops[0] is RegistryOpSpec stored
            && stored.Hive == op.Hive
            && stored.Subkey.Equals(op.Subkey, StringComparison.OrdinalIgnoreCase)
            && stored.Value.Equals(op.Value, StringComparison.Ordinal));

    /// <summary>Removes user entries by id. Shipped entries are not touchable this way and never should be.</summary>
    /// <returns>How many were actually removed.</returns>
    public int Remove(params string[] entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);

        var existing = Load();
        if (existing is null || entryIds.Length == 0)
        {
            return 0;
        }

        var removing = entryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = existing.Entries.Where(e => !removing.Contains(e.Id)).ToList();
        var removed = existing.Entries.Count - kept.Count;

        if (removed == 0)
        {
            return 0;
        }

        Save(existing with { Entries = kept });
        return removed;
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
