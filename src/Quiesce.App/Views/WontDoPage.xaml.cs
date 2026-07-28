namespace Quiesce.App.Views;

public partial class WontDoPage
{
    private static readonly RefusalRow[] Refusals =
    [
        new("Disable Windows Defender",
            "Real-time protection off means every file you download runs unscanned. The stutter Defender causes " +
            "has a narrower fix - excluding your game directories - which Quiesce offers instead, clearly labelled. " +
            "(On this machine Tamper Protection would block a disable anyway; Quiesce declines on principle, not " +
            "because it is blocked.)"),

        new("Permanently disable Windows Update",
            "Unpatched Windows on a machine that runs game launchers, mods and Discord is how machines join botnets. " +
            "Quiesce will pause update downloads during a session and resume them after - never disable the service."),

        new("Disable the paging file",
            "No pagefile means no crash dumps when something does go wrong, and commit-limit crashes in exactly the " +
            "games that spike memory. 'Free RAM' from this is an accounting trick, not a performance win."),

        new("Uninstall Store apps / remove provisioned packages",
            "Appx removal cannot be faithfully undone - reinstall is not guaranteed byte-identical, and removing the " +
            "wrong framework package silently breaks unrelated apps. Quiesce disables and suppresses bloat via the " +
            "registry, which restores exactly."),

        new("Delete registry keys",
            "Deleting a key destroys every sibling value and subkey under it, and no snapshot short of a full hive " +
            "backup can faithfully restore that. Quiesce edits individual values only, and records whether each " +
            "existed before."),

        new("Registry 'cleaning'",
            "Registry cleaners are a discredited category: orphaned keys cost nothing measurable, and every cleaner " +
            "eventually deletes something load-bearing. There is no performance to find here."),

        new("Process suspension / freezing",
            "Suspending the wrong process deadlocks audio, and kernel anti-cheat treats suspend handles near a " +
            "protected game as cheat tooling - an EasyAntiCheat ban propagates to every EAC title on your hardware. " +
            "Quiesce closes gracefully or throttles with documented APIs instead. There is zero suspension code in " +
            "the binary, enforced by CI."),

        new("DLL injection, overlays, D3D hooks",
            "Same anti-cheat exposure, plus overlay conflicts are themselves a common stutter source. Quiesce " +
            "contains no injection primitives - the build fails if any are introduced."),

        new("REALTIME process priority",
            "Raising a game above the input stack, audio engine and compositor makes average FPS go up while the " +
            "experience gets worse - input lag, audio crackle, worse frame pacing. The cap is AboveNormal, compiled in."),

        new("RAM 'cleaners' and standby-list purging",
            "Emptying working sets converts a cosmetic Task Manager number into real disk reads mid-game. On a " +
            "machine with plenty of free RAM the standby list is your friend. Quiesce lowers background apps' " +
            "memory priority instead, which is the documented mechanism."),

        new("A built-in FPS counter or 'boost score'",
            "Quiesce does not measure frames, so it will not imply that it does. If you want to verify results, " +
            "use PresentMon or CapFrameX and compare 1% lows - that is where the effect is."),

        // Investigated because it was asked for, and refused because the investigation came back negative.
        // This row exists so the answer is visible in the app rather than living only in a commit message:
        // "there is no switch for this" is a finding, and hiding it would leave the user assuming Quiesce
        // just had not got around to Phone Link yet.
        new("A 'turn off Phone Link' toggle",
            "Measured on this machine, there is nothing honest to put behind that switch. PhoneExperienceHost.exe " +
            "has no window, so it cannot be asked to close. Its sign-in task is ALREADY disabled - and it is " +
            "running anyway, because Windows activates it as a background COM server; every one of the 11 " +
            "activations in a week followed the cross-device Resume feature starting, seconds earlier. The " +
            "per-user switches in Settings are read by the app AFTER it has already started, so they change what " +
            "it does, not whether it runs. The one policy that gates the launch is documented by Microsoft for " +
            "Insider Preview only, needs elevation and a reboot, adds another 'managed by your organization' " +
            "banner, and un-links your phone in a way no registry restore can put back. Quiesce can still " +
            "throttle it from the Running apps page, and that is described as a throttle because that is what it " +
            "is. A row labelled 'off' that left the process running would be the tool lying to you."),
    ];

    public WontDoPage()
    {
        InitializeComponent();
        RefusalList.ItemsSource = Refusals;
    }
}

public sealed record RefusalRow(string Title, string Reason);
