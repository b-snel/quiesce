using Quiesce.Core;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

public class ProcessClassifierTests
{
    private static readonly string[] GameDirs =
    [
        @"C:\Program Files (x86)\Overwatch",
        @"D:\SteamLibrary\steamapps\common\Elden Ring",
    ];

    private readonly FakeProcessControl _processes = new();
    private readonly ProcessClassifier _classifier = new(GameDirs);

    [Fact]
    public void Protected_images_are_never_touchable_even_without_a_readable_path()
    {
        // Deliberately name-based: these are protected because of what they are, and the decision
        // has to hold when the path cannot be read — which for csrss and lsass is the normal case.
        foreach (var name in new[] { "explorer", "csrss", "lsass", "dwm", "audiodg", "msedgewebview2" })
        {
            var p = _processes.Add(name, imagePath: null);
            Assert.Equal(ProcessClass.NeverTouch, _classifier.Classify(p));
        }
    }

    [Fact]
    public void An_unreadable_path_resolves_to_never_touch_not_to_a_name_based_guess()
    {
        // The whole reason targeting is path-based. "Something called chrome.exe, location unknown"
        // is exactly the case name matching gets wrong, and the cost of guessing is closing a
        // program nobody asked Quiesce to close.
        var p = _processes.Add("chrome", imagePath: null);

        Assert.Equal(ProcessClass.NeverTouch, _classifier.Classify(p));
    }

    [Fact]
    public void A_game_under_a_discovered_directory_is_classified_as_a_game()
    {
        // Overwatch installs beside the Battle.net root, not under it, so launcher-root matching
        // alone would miss it entirely.
        var p = _processes.Add("Overwatch", @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe");

        Assert.Equal(ProcessClass.Game, _classifier.Classify(p));
    }

    [Fact]
    public void A_game_directory_is_matched_as_a_path_prefix_not_a_substring()
    {
        // "C:\Program Files (x86)\Overwatch" must not match a sibling directory that merely starts
        // with the same characters.
        var sibling = _processes.Add("Setup", @"C:\Program Files (x86)\OverwatchLauncherTools\Setup.exe");

        Assert.Equal(ProcessClass.Ordinary, _classifier.Classify(sibling));
    }

    [Fact]
    public void Launcher_and_anti_cheat_components_are_never_touchable()
    {
        foreach (var path in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steam.exe",
                     @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
                     @"C:\Program Files (x86)\EasyAntiCheat\EasyAntiCheat.exe",
                     @"C:\Riot Games\Riot Vanguard\vgtray.exe",
                 })
        {
            var p = _processes.Add(Path.GetFileNameWithoutExtension(path), path);
            Assert.Equal(ProcessClass.LauncherOrAntiCheat, _classifier.Classify(p));
        }
    }

    [Fact]
    public void A_launchers_embedded_chromium_is_not_treated_as_a_browser()
    {
        // Epic and Battle.net both ship a Chromium named chrome.exe inside their own tree. Closing
        // it "as a browser" tears the launcher's UI out from under a running game, so the launcher
        // test has to come first.
        var p = _processes.Add("chrome", @"C:\Program Files (x86)\Battle.net\Browser\chrome.exe");

        Assert.Equal(ProcessClass.LauncherOrAntiCheat, _classifier.Classify(p));
    }

    [Fact]
    public void A_real_browser_is_classified_as_a_browser()
    {
        var p = _processes.Add("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        Assert.Equal(ProcessClass.Browser, _classifier.Classify(p));
    }

    [Fact]
    public void Everything_else_is_ordinary()
    {
        var p = _processes.Add("Spotify", @"C:\Users\someone\AppData\Roaming\Spotify\Spotify.exe");

        Assert.Equal(ProcessClass.Ordinary, _classifier.Classify(p));
    }

    [Fact]
    public void A_recycled_pid_reads_as_absent_rather_than_as_the_original()
    {
        // The window that matters: engage records an identity, the user plays for hours, restore
        // looks the PID up again and by then it belongs to something else. Restoring a captured
        // priority onto an unrelated process is a silent mutation of a program Quiesce never touched.
        var original = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");
        _processes.Recycle(original.Identity, "notepad", @"C:\Windows\System32\notepad.exe");

        var requeried = _processes.Query(original.Identity);

        Assert.False(requeried.Present);
    }

    [Fact]
    public void An_exited_process_reads_as_absent()
    {
        var p = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");
        _processes.Exit(p.Identity);

        Assert.False(_processes.Query(p.Identity).Present);
    }

    [Fact]
    public void A_process_with_no_readable_creation_time_is_never_touchable()
    {
        // csrss, lsass, winlogon, services and smss deny StartTime to an unelevated caller. They are
        // listed rather than dropped from the inventory, so the classifier has to be the thing that
        // refuses them — and it must refuse on the missing identity, not merely on the name, because
        // without a creation time nothing can be journalled and revert could not verify it is still
        // the same process.
        var p = _processes.Add("SomeVendorService", @"C:\Program Files\Vendor\svc.exe") with
        {
            CreationTimeKnown = false,
        };

        Assert.Equal(ProcessClass.NeverTouch, _classifier.Classify(p));
    }

    [Fact]
    public void A_service_host_is_refused_at_the_process_layer()
    {
        // The hole this closes: every service guardrail — tier-0, co-tenancy, the remote-session
        // lock — is keyed on SERVICE NAMES and cannot see a request aimed at a PID. svchost.exe hosts
        // DcomLaunch, which is tier-0 as a service, while the process itself looks entirely ordinary.
        // On the dev machine this was masked unelevated, because svchost denies its image path and was
        // refused for that reason instead; elevated it would have read as a fair-game Ordinary.
        var host = _processes.Add("svchost", @"C:\Windows\System32\svchost.exe");
        var classifier = new ProcessClassifier(GameDirs, new HashSet<uint> { (uint)host.Identity.Pid });

        Assert.Equal(ProcessClass.ServiceHost, classifier.Classify(host));

        // And it must not depend on the path being readable, since these routinely deny it.
        Assert.Equal(ProcessClass.ServiceHost, classifier.Classify(host with { ImagePath = null }));
    }

    [Fact]
    public void A_non_host_process_sharing_nothing_with_the_scm_stays_ordinary()
    {
        var app = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");
        var classifier = new ProcessClassifier(GameDirs, new HashSet<uint> { 4242 });

        Assert.Equal(ProcessClass.Ordinary, classifier.Classify(app));
    }

    [Fact]
    public void With_no_game_directories_a_game_path_is_merely_ordinary()
    {
        // Guards against the allowlist silently defaulting to something. An empty allowlist must
        // mean "no games known", not "everything is a game" or "everything is protected".
        var classifier = new ProcessClassifier();
        var p = _processes.Add("Overwatch", @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe");

        Assert.Equal(ProcessClass.Ordinary, classifier.Classify(p));
    }
}
