using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;

namespace Quiesce.Core.Startup;

/// <summary>
/// What runs at sign-in, and the catalog entry that would switch one off.
/// </summary>
/// <remarks>
/// SAME DIVISION OF LABOUR AS <see cref="RunningAppDiscovery"/>: this proposes, the catalog targets. The
/// difference is what the proposal turns into. A running app becomes a process op with no inverse; an
/// auto-start entry becomes an ordinary <see cref="RegistryOpSpec"/> writing Explorer's own approval
/// value — which means it is journalled, verified by re-read, and undone byte-for-byte like every other
/// registry change. No new op kind, and none needed.
/// <para>
/// The scope is <see cref="TweakScope.Persistent"/>, which is the whole point and was an explicit product
/// decision. Session scope cannot deliver "stays off across a reboot": boot recovery auto-reverts
/// Session-scoped steps once the boot has passed, which is exactly the moment the change needed to still
/// be in force. Persistent makes it a standing preference in the same family as the debloat rows, undone
/// only when the user says so.
/// </para>
/// </remarks>
public sealed class StartupItemDiscovery(IStartupInventory inventory)
{
    /// <summary>Locations swept, in the order they are reported.</summary>
    private static readonly StartupLocation[] Locations =
    [
        StartupLocation.UserRun,
        StartupLocation.UserStartupFolder,
        StartupLocation.MachineRun,
        StartupLocation.MachineRun32,
        StartupLocation.MachineStartupFolder,
    ];

    public IReadOnlyList<StartupItem> Discover()
    {
        var items = new List<StartupItem>();

        foreach (var location in Locations)
        {
            items.AddRange(inventory.Read(location));
        }

        // Items still enabled first: those are the ones there is anything to do about. Within that,
        // grouped by location so the per-user set — the half that needs no elevation — reads together.
        return
        [
            .. items
                .OrderBy(i => i.AlreadyDisabled)
                .ThenBy(i => i.Location)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Builds the entry that stops one item running at sign-in.
    /// </summary>
    /// <remarks>
    /// The lean data is derived from the blob observed right now rather than being a canonical
    /// "disabled" constant. That is what lets an entry the user already switched off by hand elide as
    /// already-lean instead of being rewritten for a cosmetic timestamp difference — and it is why
    /// <see cref="StartupApproval.Disable"/> preserves the trailing bytes.
    /// </remarks>
    public static CatalogEntry EntryFor(StartupItem item, IReadOnlySet<string>? takenIds = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.CanDisable)
        {
            throw new CatalogException(
                $"'{item.Name}' is a {item.Location} and cannot be switched off through the registry. " +
                "Quiesce will not pretend otherwise.");
        }

        if (Win32StartupInventory.ApprovalKey(item.Location) is not { } approval)
        {
            throw new CatalogException($"No approval key is known for {item.Location}.");
        }

        var hive = approval.Hive == Microsoft.Win32.RegistryHive.CurrentUser
            ? CatalogHive.HKCU
            : CatalogHive.HKLM;

        var id = UniqueId($"startup.off-{Slug(item.Name)}", takenIds);

        return new CatalogEntry
        {
            Id = id,
            Category = "startup",
            Title = $"Do not start {Trim(item.Name)} at sign-in",
            // Situational for the same reason a user-added app group is: nothing has been measured about
            // this program. What is known is that it currently runs at every sign-in.
            Evidence = Evidence.Situational,
            Impact = Impact.Medium,
            RiskTier = 1,
            // Persistent, deliberately. See the class remarks: Session scope would be auto-reverted by the
            // very reboot the change exists to survive.
            Scope = TweakScope.Persistent,
            RequiresAdmin = item.NeedsAdmin,
            // NOT a reboot-requiring change. The approval value takes effect the moment it is written; what
            // it governs is a future sign-in. Flagging it would raise the restart banner over a machine
            // whose current session is in no way stale, which would make that banner mean less.
            RequiresReboot = false,
            Ops =
            [
                new RegistryOpSpec
                {
                    Hive = hive,
                    Subkey = approval.Subkey,
                    Value = item.Name,
                    ExpectedKind = "Binary",
                    LeanData = JsonSerializer.SerializeToElement(
                        Convert.ToBase64String(StartupApproval.Disable(item.ApprovalBlob))),
                },
            ],
            WhatItBreaks =
                $"{Trim(item.Name)} no longer starts when you sign in. It is not uninstalled and not " +
                "blocked from running — launch it yourself any time. This is the same switch as Task " +
                "Manager's Startup tab, so Task Manager will show it as Disabled. Restore puts the value " +
                "back exactly, including removing it entirely if there was none before.",
            Notes =
                $"Added from the startup list. Currently {StartupApproval.Describe(item.ApprovalBlob)}; " +
                $"source {item.Location}; command: {item.Command}. " +
                "UNVERIFIED ACROSS A SIGN-IN: nothing in the approval blob records who wrote it and Task " +
                "Manager is just another user-mode writer, so there is no mechanism by which Explorer " +
                "could tell — but that is reasoning, not a measurement. Confirm by signing out and back in."
                + (item.Location == StartupLocation.UserRun
                    ? " NOTE: a Run value is only one surface. If this program also registers a " +
                      "logon scheduled task, switching this off will not stop that task."
                    : string.Empty),
        };
    }

    private static string Trim(string name) =>
        name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

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
        var chars = Trim(name).Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var slug = new string(chars).Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Length == 0 ? "item" : slug;
    }
}
