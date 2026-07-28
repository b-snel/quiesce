using Quiesce.Core;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

public class ProcessCloserTests
{
    private static readonly string[] GameDirs = [@"D:\SteamLibrary\steamapps\common\Elden Ring"];

    private readonly FakeProcessControl _processes = new();

    private ProcessCloser CloserWith(IReadOnlySet<uint>? hostPids = null) =>
        new(_processes, new ProcessClassifier(GameDirs, hostPids));

    private static ProcessSnapshot Game(FakeProcessControl p) =>
        p.Add("eldenring", @"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe");

    [Fact]
    public void An_ordinary_windowed_process_closes()
    {
        var app = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");

        var outcome = CloserWith().Close(app.Identity);

        Assert.Equal(ProcessCloseResult.Closed, outcome.Result);
        Assert.True(outcome.Succeeded);
        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", outcome.ImagePath);
    }

    [Fact]
    public void A_process_that_declines_is_left_running_and_reported()
    {
        // The entire point of a graceful ladder. An application sitting on a save prompt has said
        // "not yet", and Quiesce has no way to insist - by design, since insisting means discarding
        // the user's unsaved work with no prompt.
        var app = _processes.Add("notepad", @"C:\Windows\System32\notepad.exe");
        _processes.RefuseToExit.Add(app.Identity.Pid);

        var outcome = CloserWith().Close(app.Identity);

        Assert.Equal(ProcessCloseResult.DeclinedToClose, outcome.Result);
        Assert.False(outcome.Succeeded);
        Assert.Contains("unsaved work", outcome.Detail);
        Assert.True(_processes.Query(app.Identity).Present, "it must still be running");

        // Asked exactly once. No retry loop, no escalation.
        Assert.Single(_processes.CloseLog);
    }

    [Fact]
    public void A_windowless_process_is_reported_as_unreachable_not_as_refusing()
    {
        // 263 of the 272 processes on the development machine have no window. Calling that a refusal
        // would blame every one of them for declining something they were never asked.
        var app = _processes.Add("SomeAgent", @"C:\Apps\Agent\agent.exe", hasWindow: false);

        var outcome = CloserWith().Close(app.Identity);

        Assert.Equal(ProcessCloseResult.NoWindow, outcome.Result);
        Assert.Empty(_processes.CloseLog);
    }

    [Fact]
    public void An_already_exited_process_is_a_success_not_a_failure()
    {
        var app = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");
        _processes.Exit(app.Identity);

        var outcome = CloserWith().Close(app.Identity);

        Assert.Equal(ProcessCloseResult.AlreadyGone, outcome.Result);
        Assert.True(outcome.Succeeded);
    }

    [Theory]
    [InlineData("explorer", @"C:\Windows\explorer.exe")]
    [InlineData("csrss", null)]
    [InlineData("steam", @"C:\Program Files (x86)\Steam\steam.exe")]
    public void Protected_classes_are_refused_before_anything_is_attempted(string image, string? path)
    {
        var app = _processes.Add(image, path);

        var outcome = CloserWith().Close(app.Identity);

        Assert.Equal(ProcessCloseResult.Refused, outcome.Result);
        Assert.NotEmpty(outcome.Detail);
        Assert.Empty(_processes.CloseLog);
        Assert.True(_processes.Query(app.Identity).Present);
    }

    [Fact]
    public void A_service_host_is_refused_with_the_service_layer_named()
    {
        var host = _processes.Add("svchost", @"C:\Windows\System32\svchost.exe");

        var outcome = CloserWith(new HashSet<uint> { (uint)host.Identity.Pid }).Close(host.Identity);

        Assert.Equal(ProcessCloseResult.Refused, outcome.Result);
        Assert.Contains("service layer", outcome.Detail);
    }

    [Fact]
    public void Nothing_is_closed_while_a_game_is_running()
    {
        // Deferred item 1 from the M4 review, closed here because this is the first code that mutates
        // process state. The realistic sequence is a slow apply plus an impatient user: the UI says
        // "applying", they alt-tab and launch a game, and a kernel anti-cheat starts mid-apply. An EAC
        // ban propagates to every EAC title on the hardware, so this is the one refusal whose
        // downside cannot be undone.
        var app = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");
        Game(_processes);

        var outcome = CloserWith().Close(app.Identity);

        Assert.Equal(ProcessCloseResult.Refused, outcome.Result);
        Assert.Contains("eldenring", outcome.Detail);
        Assert.Contains("cannot be undone", outcome.Detail);
        Assert.Empty(_processes.CloseLog);
    }

    [Fact]
    public void A_running_launcher_alone_does_not_block_closing()
    {
        // Blocking on launchers looks more cautious and would make the feature inert: Steam and Riot
        // Vanguard run essentially permanently on a gaming machine, so their presence would refuse
        // every close forever on exactly the machines this tool targets.
        var app = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");
        _processes.Add("steam", @"C:\Program Files (x86)\Steam\steam.exe");
        _processes.Add("vgtray", @"C:\Riot Games\Riot Vanguard\vgtray.exe");

        var outcome = CloserWith().Close(app.Identity);

        Assert.Equal(ProcessCloseResult.Closed, outcome.Result);
    }

    [Fact]
    public void A_recycled_pid_is_not_closed_in_place_of_the_original()
    {
        // Engage records an identity; hours later restore or a retry asks again, and by then the PID
        // belongs to something else. Closing "the process at that PID" would close a program Quiesce
        // was never asked to touch.
        var original = _processes.Add("Spotify", @"C:\Apps\Spotify\Spotify.exe");
        var replacement = _processes.Recycle(original.Identity, "notepad", @"C:\Windows\System32\notepad.exe");

        var outcome = CloserWith().Close(original.Identity);

        Assert.Equal(ProcessCloseResult.AlreadyGone, outcome.Result);
        Assert.True(_processes.Query(replacement.Identity).Present, "the replacement must be untouched");
        Assert.Empty(_processes.CloseLog);
    }

    [Fact]
    public void A_browser_is_closable_because_the_class_permits_it()
    {
        var browser = _processes.Add("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        Assert.Equal(ProcessCloseResult.Closed, CloserWith().Close(browser.Identity).Result);
    }
}
