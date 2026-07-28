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
    ];

    public WontDoPage()
    {
        InitializeComponent();
        RefusalList.ItemsSource = Refusals;
    }
}

public sealed record RefusalRow(string Title, string Reason);
