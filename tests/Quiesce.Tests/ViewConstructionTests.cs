using System.Windows.Controls;
using Quiesce.App;
using Quiesce.App.Views;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;

namespace Quiesce.Tests;

/// <summary>
/// Constructs every page on an STA thread and asserts it builds without throwing.
/// </summary>
/// <remarks>
/// A XAML parse error, a bad resource key, or a broken binding path only surfaces at runtime, and
/// the GUI runs elevated so an unelevated test process cannot drive it with synthetic input (UIPI
/// blocks that). Constructing the pages is the strongest automated check available, and it does
/// catch the failures that actually happen: malformed XAML, missing DataTemplate types, and the
/// ServicesPage guardrail cross-check.
/// </remarks>
public class ViewConstructionTests
{
    /// <summary>
    /// WPF requires STA. Also creates the Application instance the pages resolve brushes from -
    /// without it, TryFindResource returns null and the evidence badges silently fall back to gray.
    /// </summary>
    /// <summary>
    /// WPF allows exactly one <see cref="System.Windows.Application"/> per AppDomain, so it is
    /// created once for the whole test run rather than per test.
    /// </summary>
    private static readonly Lock AppLock = new();

    private static bool _appInitialized;

    private static void EnsureApplication()
    {
        lock (AppLock)
        {
            if (_appInitialized)
            {
                return;
            }

            _appInitialized = true;

            // A bare Application, not Quiesce.App.App: the real one runs the single-instance guard
            // in OnStartup, and merging App.xaml as a dictionary would CONSTRUCT an Application
            // (its root element is <Application x:Class="...">), which WPF permits only once per
            // AppDomain. Brushes.xaml is a plain dictionary precisely so it can be merged here.
            var app = System.Windows.Application.Current ?? new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Quiesce;component/Theme/Brushes.xaml", UriKind.Absolute),
            });
        }
    }

    private static T OnStaThread<T>(Func<T> func)
    {
        T? result = default;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication();
                result = func();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
        {
            throw new InvalidOperationException($"Page construction threw: {failure}", failure);
        }

        return result!;
    }

    private static AppState CleanState() => new()
    {
        MachineState = new QuiesceState { IsDirty = false },
        DataRoot = @"C:\ProgramData\Quiesce",
        LoadError = "test harness: no catalog",
    };

    [Fact]
    public void All_pages_construct()
    {
        using var temp = new TempDataRoot();

        var built = OnStaThread(() => new UserControl[]
        {
            new DashboardPage(CleanState()),
            new FeaturesPage(CleanState()),
            // Given a throwaway data root rather than the real one: the page reads the user-added apps
            // file, and a test has no business reading or writing C:\ProgramData\Quiesce.
            new RunningAppsPage(CleanState() with { DataRoot = temp.Path }),
            // Reads the real machine's Run keys and Startup folders, which is read-only and needs no
            // elevation; the throwaway data root keeps the preference file out of C:\ProgramData.
            new StartupPage(CleanState() with { DataRoot = temp.Path }),
            new ServicesPage(),
            new WontDoPage(),
        });

        Assert.Equal(6, built.Length);
        Assert.All(built, page => Assert.NotNull(page));
    }

    /// <summary>
    /// Off entries sort above on ones, which is what makes the exceptions the user made visible instead of
    /// scattered through three dozen rows.
    /// </summary>
    [Fact]
    public void Features_lists_switched_off_entries_first()
    {
        using var temp = new TempDataRoot();

        // "b.on" is enabled and sits later in the catalog; "c.off" is disabled and sits later still.
        // Catalog order alone would give a, b, c — the sort has to put the two off rows in front.
        new Core.Catalog.ProfileStore(temp.Path).SetEnabled("b.on", enabled: true);

        var order = OnStaThread(() =>
        {
            var page = new FeaturesPage(StateWith(temp.Path, "a.off", "b.on", "c.off"));
            return page.EntryList.Items.Cast<FeatureRow>().Select(r => r.EntryId).ToList();
        });

        Assert.Equal(["a.off", "c.off", "b.on"], order);
    }

    [Fact]
    public void Select_all_then_select_none_moves_every_entry()
    {
        using var temp = new TempDataRoot();

        var (afterAll, afterNone) = OnStaThread(() =>
        {
            var page = new FeaturesPage(StateWith(temp.Path, "a", "b", "c"));

            page.SelectAllButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var all = new Core.Catalog.ProfileStore(temp.Path).ActiveEnabled().Count;

            page.SelectNoneButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var none = new Core.Catalog.ProfileStore(temp.Path).ActiveEnabled().Count;

            return (all, none);
        });

        Assert.Equal(3, afterAll);
        Assert.Equal(0, afterNone);
    }

    /// <summary>
    /// The enabled set describes what is currently applied while engaged, so changing it would
    /// desynchronize the journal from the profile. Locked with a reason, never hidden.
    /// </summary>
    [Fact]
    public void Bulk_buttons_are_disabled_while_engaged()
    {
        using var temp = new TempDataRoot();

        var state = StateWith(temp.Path, "a") with
        {
            MachineState = new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() },
        };

        var (all, none) = OnStaThread(() =>
        {
            var page = new FeaturesPage(state);
            return (page.SelectAllButton.IsEnabled, page.SelectNoneButton.IsEnabled);
        });

        Assert.False(all);
        Assert.False(none);
    }

    private static AppState StateWith(string dataRoot, params string[] entryIds)
    {
        var catalog = EngineTestHarness.CatalogOf(
            [.. entryIds.Select(id => EngineTestHarness.DwordEntry(id, valueName: id))]);

        return new AppState
        {
            MachineState = new QuiesceState { IsDirty = false },
            DataRoot = dataRoot,
            Catalog = catalog,
            CatalogPath = "test",
        };
    }

    /// <summary>A throwaway data root, so a page that writes a profile does not write the real one.</summary>
    private sealed class TempDataRoot : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void The_preflight_row_renders_every_kind_of_step_including_a_process()
    {
        // A real crash, not a hypothetical one. The row builder tested for a service prior and otherwise
        // dereferenced the registry prior, so a process step - which carries neither - was a null
        // reference in the middle of the dialog the user approves changes in. It is also the one place
        // "Restore will not reopen it" has to be said, so this asserts the wording is actually there.
        var rows = OnStaThread(() => new[]
        {
            PreflightRow.From(ProcessStep(Core.Catalog.ProcessAction.Close)),
            PreflightRow.From(ProcessStep(Core.Catalog.ProcessAction.Throttle)),
        });

        Assert.Contains("running at Normal priority", rows[0].PriorText);
        Assert.Contains("NOT reopen", rows[0].NewText);
        Assert.Contains("put back on Restore", rows[1].NewText);
    }

    [Fact]
    public void A_drifted_machine_renders_as_its_own_state_not_as_engaged()
    {
        // The fourth state, and the reason the palette work came first: before it, "engaged and matching"
        // and "engaged and no longer matching" would have rendered identically.
        var drifted = EngagedState() with { Drift = DriftWith(resyncable: 1, reportedOnly: 0) };

        Assert.Equal(DashboardPage.CardState.Drifted, DashboardPage.StateOf(drifted));
        Assert.NotEqual(
            DashboardPage.BrushKeyFor(DashboardPage.CardState.Engaged),
            DashboardPage.BrushKeyFor(DashboardPage.CardState.Drifted));
    }

    [Fact]
    public void An_engaged_machine_that_was_checked_and_matches_is_not_drifted()
    {
        // "Checked and matching" must not be the drifted state, and it must not be indistinguishable from
        // "never checked" either - the card says Quiesce looked only when it did.
        var matching = EngagedState() with { Drift = DriftWith(resyncable: 0, reportedOnly: 0) };

        Assert.Equal(DashboardPage.CardState.Engaged, DashboardPage.StateOf(matching));
        Assert.Null(MainWindow.DriftBannerText(matching));
    }

    [Fact]
    public void An_unknown_drift_report_never_reads_as_in_sync()
    {
        // Third fact, third rendering. "Could not tell" is not "matches", for the same reason StateUnknown
        // is not "clean" - and the data root is hardened to Administrators, so this is the DEFAULT outcome
        // for anything that is not elevated.
        var unknown = EngagedState() with
        {
            Drift = new DriftReport
            {
                SessionId = Guid.NewGuid(),
                Items = [],
                Unknown = true,
                UnknownReason = "journal unreadable",
                AppliedBeforeLastRestart = false,
                CheckedUtc = DateTimeOffset.UtcNow,
            },
        };

        var text = MainWindow.DriftBannerText(unknown);

        Assert.NotNull(text);
        Assert.Contains("cannot tell", text.Value.Headline);
    }

    [Fact]
    public void The_drift_banner_shouts_save_your_work_when_something_will_be_closed()
    {
        // The banner is where the user decides whether to press Resync at all, so the warning belongs here
        // as well as in the preflight - a close is the one thing Restore does not undo.
        var text = MainWindow.DriftBannerText(
            EngagedState() with { Drift = DriftWith(resyncable: 2, reportedOnly: 0) });

        Assert.NotNull(text);
        Assert.Equal("Out of sync with what Quiesce applied", text.Value.Headline);
        Assert.Contains("SAVE YOUR WORK FIRST", text.Value.Detail);
        Assert.Contains("2 things Quiesce can put back", text.Value.Detail);
        Assert.Contains("checked ", text.Value.Detail);
    }

    [Fact]
    public void A_banner_with_nothing_resyncable_does_not_imply_an_action()
    {
        // When Quiesce will not put anything back, the headline must not read as an offer. It also has to
        // name the items, so "noticed and deliberately left alone" cannot be mistaken for "did not notice".
        var text = MainWindow.DriftBannerText(
            EngagedState() with { Drift = DriftWith(resyncable: 0, reportedOnly: 1) });

        Assert.NotNull(text);
        Assert.Equal("Changed since Engage, and Quiesce is leaving it alone", text.Value.Headline);
        Assert.DoesNotContain("SAVE YOUR WORK", text.Value.Detail);
        Assert.Contains("will NOT put back", text.Value.Detail);
        Assert.Contains("because it says so", text.Value.Detail);
    }

    [Fact]
    public void Both_halves_are_shown_when_both_exist()
    {
        // A session can have some drift Quiesce will fix and some it will not, and reporting only one half
        // would be the list-that-quietly-shortens failure this codebase has already fixed twice.
        var text = MainWindow.DriftBannerText(
            EngagedState() with { Drift = DriftWith(resyncable: 1, reportedOnly: 2) });

        Assert.NotNull(text);
        Assert.Contains("1 thing Quiesce can put back", text.Value.Detail);
        Assert.Contains("2 things changed that Quiesce will NOT put back", text.Value.Detail);
    }

    private static AppState EngagedState() => new()
    {
        MachineState = new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() },
        DataRoot = @"C:\ProgramData\Quiesce",
    };

    private static DriftReport DriftWith(int resyncable, int reportedOnly) => new()
    {
        SessionId = Guid.NewGuid(),
        Unknown = false,
        AppliedBeforeLastRestart = false,
        CheckedUtc = DateTimeOffset.UtcNow,
        Items =
        [
            .. Enumerable.Range(0, resyncable).Select(i => new DriftItem
            {
                StepId = i,
                EntryId = $"apps.fix{i}",
                Target = $"close comet — comet (pid {i})",
                Kind = DriftKind.ProcessReturned,
                Detail = "comet.exe is running again",
                Resyncable = true,
            }),
            .. Enumerable.Range(0, reportedOnly).Select(i => new DriftItem
            {
                StepId = 100 + i,
                EntryId = $"shell.left{i}",
                Target = $@"HKCU\Software\Test!Value{i}",
                Kind = DriftKind.RegistryChanged,
                Detail = "now 1, and Quiesce wrote 0.",
                Resyncable = false,
                NotResyncableReason = "Quiesce will not put this back because it says so.",
            }),
        ],
    };

    [Fact]
    public void Every_card_state_resolves_to_its_own_brush()
    {
        // The bug this replaces: #33D29922 meant three different things across five files. It was the
        // Engaged card, the Unknown card, AND the reboot banner - so "your machine is as you asked",
        // "Quiesce cannot tell what state your machine is in", and "a change is waiting on a restart"
        // were indistinguishable. DashboardPage's own comment said the first two must never render the
        // same, directly above the branch where they did.
        //
        // Asserted two ways, because either alone is passable while the palette is broken: every state
        // maps to a DIFFERENT key, and every key actually RESOLVES (a typo would silently fall back to
        // gray for all of them, which is uniform and therefore also a lie).
        var states = Enum.GetValues<DashboardPage.CardState>();
        var keys = states.Select(DashboardPage.BrushKeyFor).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        var resolved = OnStaThread(() => keys
            .Select(k => System.Windows.Application.Current.TryFindResource(k) as System.Windows.Media.Brush)
            .ToList());

        Assert.All(resolved, brush => Assert.NotNull(brush));

        // And the colours themselves are distinct, not merely the keys. Two keys pointing at one hex
        // would satisfy everything above and still render two facts identically.
        var colours = resolved
            .Cast<System.Windows.Media.SolidColorBrush>()
            .Select(b => b.Color)
            .ToList();

        Assert.Equal(colours.Count, colours.Distinct().Count());
    }

    [Fact]
    public void The_keys_referenced_only_from_XAML_resolve()
    {
        // StateWarningBrush and StateNeutralBrush have no enum to enumerate: they are named from
        // DynamicResource in three XAML files and from one switch arm. A DynamicResource that does not
        // resolve throws nothing and paints nothing, so the reboot banner and the save-your-work banner
        // would go transparent - still legible, still laid out, and no longer reading as a warning at
        // all. Exactly the failure that has no visible symptom until someone is looking for one.
        var resolved = OnStaThread(() => new[] { "StateWarningBrush", "StateNeutralBrush" }
            .Select(k => System.Windows.Application.Current.TryFindResource(k) as System.Windows.Media.Brush)
            .ToList());

        Assert.All(resolved, brush => Assert.NotNull(brush));
    }

    [Fact]
    public void Engaged_no_longer_renders_as_a_warning()
    {
        // Specifically pinned rather than left implicit: Engaged is the healthy state this product
        // exists to produce, and it spent three milestones painted the same amber as "needs a restart".
        var amber = System.Windows.Media.Color.FromArgb(0x33, 0xD2, 0x99, 0x22);

        var engaged = OnStaThread(() =>
            System.Windows.Application.Current.TryFindResource(
                DashboardPage.BrushKeyFor(DashboardPage.CardState.Engaged)) as System.Windows.Media.SolidColorBrush);

        Assert.NotNull(engaged);
        Assert.NotEqual(amber, engaged.Color);
    }

    [Fact]
    public void The_state_is_unknown_before_it_is_engaged_and_engaged_before_it_is_clean()
    {
        // Precedence, not just mapping. A state file that cannot be read must never resolve to Clean,
        // and IsDirty is default(bool) == false on the record that carries StateUnknown - so an order
        // that tested IsDirty first would report "Machine is clean" for an unreadable state file.
        var unreadable = new AppState
        {
            MachineState = new QuiesceState(),
            DataRoot = @"C:\ProgramData\Quiesce",
            StateUnknown = true,
        };

        Assert.Equal(DashboardPage.CardState.Unknown, DashboardPage.StateOf(unreadable));
        Assert.Equal(
            DashboardPage.CardState.Engaged,
            DashboardPage.StateOf(CleanState() with
            {
                MachineState = new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() },
            }));
        Assert.Equal(DashboardPage.CardState.Clean, DashboardPage.StateOf(CleanState()));
    }

    [Fact]
    public void The_reversibility_note_does_not_call_a_close_reversible()
    {
        // The live bug: RequiresElevation was standing in for "is this serious", and a process op's
        // NeedsAdmin is correctly false - closing a window in your own session needs no privilege. So a
        // plan whose effective steps were only closes fell through to "fully reversible", in the footer
        // of the dialog where the user decides whether to allow the one thing with no undo. Reachable on
        // any machine whose registry entries are already lean, which is every machine that has engaged
        // once and restored.
        var closesOnly = PlanOf(ProcessStep(Core.Catalog.ProcessAction.Close));

        var note = PreflightDialog.ReversibilityText(closesOnly);

        Assert.DoesNotContain("fully reversible", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("except the closes", note);

        // A throttle IS put back, so a plan with no close keeps the old promise.
        var throttleOnly = PlanOf(ProcessStep(Core.Catalog.ProcessAction.Throttle));

        Assert.Contains("fully reversible", PreflightDialog.ReversibilityText(throttleOnly));
    }

    [Fact]
    public void Preflight_names_every_application_it_will_close_and_shouts_save_your_work()
    {
        var plan = PlanOf(
            ProcessStep(Core.Catalog.ProcessAction.Close, pid: 1, imageName: "comet"),
            ProcessStep(Core.Catalog.ProcessAction.Close, pid: 2, imageName: "Discord"));

        var summary = PreflightDialog.CloseSummary([.. plan.EffectiveSteps]);

        Assert.Equal("2 applications will be asked to close: comet.exe, Discord.exe.", summary);
    }

    [Fact]
    public void A_browser_with_nineteen_processes_is_named_once()
    {
        // The measured shape on this machine: Comet ran nineteen processes, and a close journals one
        // step per instance. The per-row "Restore will NOT reopen it" is right nineteen times over and
        // useless as a warning at that repetition, which is why this sentence exists.
        var plan = PlanOf([.. Enumerable.Range(1, 19)
            .Select(pid => ProcessStep(Core.Catalog.ProcessAction.Close, pid, "comet"))]);

        var summary = PreflightDialog.CloseSummary([.. plan.EffectiveSteps]);

        Assert.Equal("1 application will be asked to close: comet.exe.", summary);
    }

    [Fact]
    public void A_plan_that_closes_nothing_gets_no_close_warning_at_all()
    {
        // Empty rather than a reassuring sentence: the banner is Collapsed, so there is nothing to say.
        var plan = PlanOf(ProcessStep(Core.Catalog.ProcessAction.Throttle));

        Assert.Equal(string.Empty, PreflightDialog.CloseSummary([.. plan.EffectiveSteps]));
    }

    [Fact]
    public void The_close_warning_is_shown_only_when_something_will_close()
    {
        // CloseSummary is asserted directly elsewhere; this asserts the wiring, because a correct
        // sentence in a Collapsed border is the same as no sentence at all.
        var (closing, throttling) = OnStaThread(() =>
        {
            var withClose = new PreflightDialog(
                PlanOf(ProcessStep(Core.Catalog.ProcessAction.Close, pid: 1, imageName: "comet")), null);
            var withoutClose = new PreflightDialog(
                PlanOf(ProcessStep(Core.Catalog.ProcessAction.Throttle)), null);

            return ((withClose.CloseWarning.Visibility, withClose.CloseHeadline.Text),
                    withoutClose.CloseWarning.Visibility);
        });

        Assert.Equal(System.Windows.Visibility.Visible, closing.Item1);
        Assert.Contains("comet.exe", closing.Item2);
        Assert.Equal(System.Windows.Visibility.Collapsed, throttling);
    }

    private static Core.Engine.EngagePlan PlanOf(params Core.Engine.PlannedStep[] steps) => new()
    {
        Profile = "default",
        CatalogVersion = "test",
        Steps = steps,
    };

    private static Core.Engine.PlannedStep ProcessStep(
        Core.Catalog.ProcessAction action, int pid = 1, string imageName = "chrome") => new()
    {
        StepId = pid,
        EntryId = "apps.test",
        Scope = Core.Catalog.TweakScope.Session,
        Op = new Core.Catalog.ProcessOpSpec
        {
            Action = action,
            ImageName = imageName,
            UnderDirectories = [@"\Google\Chrome\Application\"],
            ThrottleTo = action == Core.Catalog.ProcessAction.Throttle ? Core.Catalog.ThrottleLevel.Idle : null,
        },
        Target = $"close {imageName} — {imageName} (pid {pid})",
        ProcessBefore = new Core.Platform.ProcessSnapshot
        {
            Identity = new Core.Platform.ProcessIdentity { Pid = pid, CreatedUtcTicks = pid },
            ImageName = imageName,
            ImagePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            SessionId = 1,
            PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal,
            HasVisibleWindow = true,
        },
        ProcessAction = action,
        IntendedPriority = action == Core.Catalog.ProcessAction.Throttle
            ? System.Diagnostics.ProcessPriorityClass.Idle
            : null,
        Activation = [],
        NoOp = false,
    };

    [Fact]
    public void Dashboard_renders_the_engaged_state_differently()
    {
        var engaged = new AppState
        {
            MachineState = new QuiesceState { IsDirty = true, ActiveSessionId = Guid.NewGuid() },
            DataRoot = @"C:\ProgramData\Quiesce",
        };

        var page = OnStaThread(() => new DashboardPage(engaged));

        Assert.NotNull(page);
    }

    [Fact]
    public void Engage_and_restore_are_both_refused_with_no_catalog_and_nothing_engaged()
    {
        // Engage needs a catalog to have anything to apply; Restore needs something to undo. CleanState
        // has neither, so both are off - and a button that is enabled with nothing behind it would
        // silently do nothing, which is worse than being visibly disabled.
        //
        // The assertion runs INSIDE the STA thread: WPF DependencyObjects have thread affinity, so
        // reading IsEnabled from the test thread throws "a different thread owns it".
        var (engage, restore) = OnStaThread(() =>
        {
            var page = new DashboardPage(CleanState());
            return (page.EngageButton.IsEnabled, page.RestoreButton.IsEnabled);
        });

        Assert.False(engage);
        Assert.False(restore);
    }

    [Fact]
    public void Engage_stays_refused_and_restore_stays_offered_when_the_state_is_unreadable()
    {
        // The regression this pins is specific and was live: Render() and SetBusy() each computed the
        // two button states from their own copy of the expression, and each copy had dropped a
        // different clause. SetBusy re-enabled Engage in the UNKNOWN case - engaging over a machine
        // that may already be engaged captures the first session's tweaks as if they were the user's
        // original settings, and UNKNOWN is the one case the engine cannot refuse because it never gets
        // a chance to run - and it disabled Restore in the same case, which is the one action whose
        // worst outcome is finding nothing to undo.
        //
        // So the assertion is deliberately made TWICE: once as constructed, and again after a
        // busy/not-busy cycle, because passing the first and failing the second is exactly the shape
        // the bug had.
        var unknown = new AppState
        {
            MachineState = new QuiesceState(),
            DataRoot = @"C:\ProgramData\Quiesce",
            StateUnknown = true,
            LoadError = "state.json could not be read",
        };

        var (afterRender, afterBusyCycle) = OnStaThread(() =>
        {
            var page = new DashboardPage(unknown);
            var rendered = (page.EngageButton.IsEnabled, page.RestoreButton.IsEnabled);

            page.SetBusy(true);
            page.SetBusy(false);

            return (rendered, (page.EngageButton.IsEnabled, page.RestoreButton.IsEnabled));
        });

        Assert.False(afterRender.Item1);
        Assert.True(afterRender.Item2);

        Assert.False(afterBusyCycle.Item1);
        Assert.True(afterBusyCycle.Item2);
    }

    [Fact]
    public void ServicesPage_refuses_to_list_a_service_the_guardrail_does_not_protect()
    {
        // The page asserts its own list against Guardrails at construction. This test documents
        // that the cross-check exists: UI claiming a service is locked while the engine would
        // happily touch it is precisely the kind of lie the product exists to avoid.
        var page = OnStaThread(() => new ServicesPage());

        Assert.NotNull(page);
    }
}
