using Quiesce.App;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;

namespace Quiesce.Tests;

/// <summary>
/// Everything the tray decides. The icon itself is verified by hand; these are the decisions.
/// </summary>
/// <remarks>
/// The <c>TaskbarIcon</c> is deliberately untested and the reason is worth stating: the STA harness never
/// calls <c>Dispatcher.Run()</c>, so a message-only window receives nothing and dies with the thread — and
/// constructing a real one would leave an icon in the test host's notification area. So every fact the tray
/// shows is a pure function over <see cref="AppState"/>, and those are asserted here.
/// </remarks>
public class TrayPresentationTests
{
    private static AppState Clean() => new()
    {
        MachineState = new QuiesceState { IsDirty = false },
        DataRoot = @"C:\ProgramData\Quiesce",
    };

    private static AppState Engaged(DriftReport? drift = null) => new()
    {
        MachineState = new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() },
        DataRoot = @"C:\ProgramData\Quiesce",
        Drift = drift,
    };

    private static AppState Unknown() => new()
    {
        MachineState = new QuiesceState(),
        DataRoot = @"C:\ProgramData\Quiesce",
        StateUnknown = true,
    };

    private static DriftReport Drift(int items, bool unknown = false) => new()
    {
        SessionId = Guid.NewGuid(),
        Unknown = unknown,
        AppliedBeforeLastRestart = false,
        CheckedUtc = DateTimeOffset.UtcNow,
        Items =
        [
            .. Enumerable.Range(0, items).Select(i => new DriftItem
            {
                StepId = i,
                EntryId = "apps.test",
                Target = "close comet",
                Kind = DriftKind.ProcessReturned,
                Detail = "comet.exe is running again",
                Resyncable = true,
            }),
        ],
    };

    [Fact]
    public void The_tray_menu_never_offers_Engage()
    {
        // THIS TEST IS THE SAFETY DECISION, not a description of it. Engage closes browsers with no undo, and
        // a tray menu is where people click by muscle memory one item from the thing they meant. Both
        // mutating paths stay behind the window and the preflight. If someone later adds an Engage item for
        // convenience, this is what stops it landing silently.
        // Matched on the LEADING WORD, not on the substring. A substring check fails on the exit item's
        // "(this machine stays engaged)" - where "engaged" is an adjective describing what quitting leaves
        // behind, which is exactly the sentence that should be there. Asserting on the action verb is the
        // assertion actually intended.
        foreach (var state in new[] { Clean(), Engaged(), Engaged(Drift(2)), Unknown() })
        {
            Assert.DoesNotContain(
                TrayPresentation.MenuItems(state),
                item => item.StartsWith("Engage", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_tray_menu_never_offers_Restore_either()
    {
        // Same reasoning one step further. Restore is safe in outcome but it would run the engine from a menu
        // callback - a second mutation path outside the App.Mutating gate, reachable while a preflight is open
        // on the window, because a modal does not disable a message-only HWND.
        Assert.DoesNotContain(
            TrayPresentation.MenuItems(Engaged()),
            item => item.Contains("restore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Exit_says_the_machine_stays_engaged_when_it_does()
    {
        // A user quitting from the tray while engaged should not have to find out later that quitting did not
        // un-engage anything. Only Restore does.
        Assert.Contains("stays engaged", TrayPresentation.MenuItems(Engaged())[3]);
        Assert.Contains("stays engaged", TrayPresentation.MenuItems(Unknown())[3]);

        // And it does not say it when it would be false.
        Assert.Equal("Exit Quiesce", TrayPresentation.MenuItems(Clean())[3]);
    }

    [Fact]
    public void The_tooltip_never_claims_the_machine_is_in_sync_when_it_has_not_looked()
    {
        // Three different facts, three different sentences. Engaged-and-never-checked must not read as
        // engaged-and-matching, because there is no background check and a stale answer presented as current
        // is the reassuring lie this project is organised against.
        var neverChecked = TrayPresentation.Tooltip(Engaged());
        var matching = TrayPresentation.Tooltip(Engaged(Drift(0)));
        var couldNotTell = TrayPresentation.Tooltip(Engaged(Drift(0, unknown: true)));

        Assert.DoesNotContain("matching", neverChecked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still matching", matching, StringComparison.Ordinal);
        Assert.Contains("cannot tell", couldNotTell, StringComparison.Ordinal);

        Assert.Equal(3, new[] { neverChecked, matching, couldNotTell }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_drifted_tooltip_carries_the_count_and_the_time_it_looked()
    {
        var tooltip = TrayPresentation.Tooltip(Engaged(Drift(2)));

        Assert.Contains("2 changes out of sync", tooltip, StringComparison.Ordinal);
        Assert.Contains("as of ", tooltip, StringComparison.Ordinal);

        // Singular, because "1 changes" is the kind of detail that makes a tool feel unfinished.
        Assert.Contains("1 change out of sync", TrayPresentation.Tooltip(Engaged(Drift(1))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_icon_asks_for_attention_for_drift_and_for_unknown_but_not_for_engaged()
    {
        // Engaged is the healthy state this product exists to produce - the icon must not nag about success.
        Assert.False(TrayPresentation.NeedsAttention(Clean()));
        Assert.False(TrayPresentation.NeedsAttention(Engaged()));
        Assert.False(TrayPresentation.NeedsAttention(Engaged(Drift(0))));

        Assert.True(TrayPresentation.NeedsAttention(Engaged(Drift(1))));
        Assert.True(TrayPresentation.NeedsAttention(Engaged(Drift(0, unknown: true))));

        // Unknown counts: Quiesce cannot answer the only question it is for, which is at least as worth
        // surfacing as drift.
        Assert.True(TrayPresentation.NeedsAttention(Unknown()));
    }

    [Fact]
    public void The_sync_check_is_disabled_with_a_reason_rather_than_hidden()
    {
        // Disabled, not hidden: an item that appears and disappears makes the menu a different shape each
        // time it is opened, and the reason is short enough to say.
        Assert.Null(TrayPresentation.SyncCheckDisabledReason(Engaged()));

        var clean = TrayPresentation.SyncCheckDisabledReason(Clean());
        Assert.NotNull(clean);
        Assert.Contains("nothing is engaged", clean, StringComparison.Ordinal);

        var unknown = TrayPresentation.SyncCheckDisabledReason(Unknown());
        Assert.NotNull(unknown);
        Assert.Contains("cannot read its own state", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_actionable_items_are_marked_as_opening_something()
    {
        // The ellipses are load-bearing. Neither item does the thing; both open the window where the thing
        // happens, behind the preflight and the mutation gate.
        var items = TrayPresentation.MenuItems(Engaged(Drift(1)));

        Assert.EndsWith("…", items[1], StringComparison.Ordinal);
        Assert.EndsWith("…", items[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_and_the_tooltip_agree()
    {
        // Two surfaces, one fact. They had no reason to differ and every reason not to: a header that
        // disagreed with the tooltip the user just hovered would make both untrustworthy.
        foreach (var state in new[] { Clean(), Engaged(), Engaged(Drift(3)), Unknown() })
        {
            Assert.Equal(TrayPresentation.Tooltip(state), TrayPresentation.Header(state));
        }
    }
}
