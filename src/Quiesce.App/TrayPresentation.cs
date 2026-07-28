using Quiesce.Core.Engine;

namespace Quiesce.App;

/// <summary>
/// What the tray shows and offers, as pure functions over state.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the <c>TaskbarIcon</c> itself because every DECISION here is testable and the icon is not.
/// <c>ViewConstructionTests.OnStaThread</c> never calls <c>Dispatcher.Run()</c>, so a message-only window
/// receives nothing and dies with the thread — and constructing a real one would leave an icon in the test
/// host's notification area. So the icon is verified by hand and everything that decides what it says lives
/// here, where it is asserted.
/// </para>
/// <para>
/// ENGAGE IS DELIBERATELY NOT AN ITEM, and a test asserts it. Engage closes browsers with no undo, and a
/// tray menu is where people click by muscle memory one item away from the thing they meant. Both mutating
/// paths stay behind the window and the preflight.
/// </para>
/// </remarks>
internal static class TrayPresentation
{
    /// <summary>Whether the icon should be drawing attention to itself.</summary>
    /// <remarks>
    /// TWO states, not five. The icon says "something needs you"; the tooltip and the disabled header say
    /// which. A five-state icon in a 16-pixel square that the user can hide in the overflow would be
    /// encoding facts where they cannot be read.
    /// </remarks>
    public static bool NeedsAttention(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Unknown counts as attention: Quiesce cannot answer the only question it is for, and that is at
        // least as worth surfacing as drift.
        return state.StateUnknown || state.Drifted || state.Drift is { Unknown: true };
    }

    /// <summary>
    /// The tooltip, which is the only thing visible without opening the menu.
    /// </summary>
    /// <remarks>
    /// It never claims the machine is in sync unless a check actually said so, and it carries the time of
    /// that check — because there is no background check and a stale answer presented as current would be
    /// the reassuring lie this project is organised against.
    /// </remarks>
    public static string Tooltip(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.StateUnknown)
        {
            return "Quiesce — cannot tell whether this machine is modified";
        }

        if (!state.MachineState.IsDirty)
        {
            return "Quiesce — machine is clean";
        }

        return state.Drift switch
        {
            null => "Quiesce — engaged",
            { Unknown: true } => "Quiesce — engaged, and cannot tell whether it still matches",
            { Any: true } drift =>
                $"Quiesce — engaged, {Count(drift.Items.Count, "change")} out of sync · as of {At(drift)}",
            var drift => $"Quiesce — engaged, still matching · checked {At(drift)}",
        };
    }

    /// <summary>The disabled header line at the top of the menu. Same facts, more room.</summary>
    public static string Header(AppState state) => Tooltip(state);

    /// <summary>
    /// The menu item headers, in order. The shape of the menu, asserted rather than eyeballed.
    /// </summary>
    /// <remarks>
    /// The ellipses are load-bearing: both middle items OPEN something rather than doing it. "Check sync…"
    /// re-detects, shows the window and raises the resync preflight — it does not resync, and it does not
    /// run the engine from a menu callback, so all mutation goes through the window and the
    /// <see cref="App.Mutating"/> gate.
    /// </remarks>
    public static IReadOnlyList<string> MenuItems(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return
        [
            "Open Quiesce",
            "Check sync…",
            "Settings…",

            // Named for what it leaves behind. Exiting does not un-engage a machine - only Restore does -
            // and a user who quits from the tray while engaged should not have to discover that later.
            state.MachineState.IsDirty || state.StateUnknown
                ? "Exit Quiesce (this machine stays engaged)"
                : "Exit Quiesce",
        ];
    }

    /// <summary>Whether the sync check has anything to check, and the reason when it does not.</summary>
    /// <remarks>
    /// Disabled with a reason rather than hidden. An item that appears and disappears makes the menu a
    /// different shape each time it is opened, and the reason is short enough to say.
    /// </remarks>
    public static string? SyncCheckDisabledReason(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.StateUnknown)
        {
            return "Quiesce cannot read its own state, so it cannot tell what to compare against";
        }

        return state.MachineState.IsDirty
            ? null
            : "nothing is engaged, so there is nothing to be out of sync with";
    }

    private static string At(DriftReport drift) =>
        drift.CheckedUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
