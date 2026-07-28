using Quiesce.Core;
using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// What the running-apps list will and will not offer, and what adding one writes.
/// </summary>
/// <remarks>
/// The property under test throughout is that discovery <em>proposes</em>. It must never surface
/// something the guardrails would refuse, and the entry it writes must be no looser than a shipped one —
/// an image name plus the directory the application was actually found in. If discovery could produce an
/// entry the catalog loader would refuse, or a match wider than the path it observed, the whole point of
/// path-based targeting would have been quietly traded away for convenience.
/// </remarks>
[Collection(ProcessAncestryCollection.Name)]
public sealed class RunningAppDiscoveryTests : IDisposable
{
    private const string GameDir = @"C:\Games\Doom";

    private readonly FakeProcessControl _processes = new();

    // Pinned rather than inherited from the real machine. The ancestry is a process-wide static, so a
    // class that reads whatever happens to be there is both machine-dependent and at the mercy of any
    // other class that sets it — which is exactly how this suite first failed.
    public RunningAppDiscoveryTests() =>
        ProcessAncestry.OverrideForTests = new HashSet<int> { Environment.ProcessId };

    public void Dispose() => ProcessAncestry.OverrideForTests = null;

    // ownImageName pinned for the same reason the ancestry is: it otherwise defaults to whatever the test
    // host executable happens to be called, which would quietly change what these tests assert.
    private RunningAppDiscovery Discovery(IReadOnlySet<uint>? hostPids = null) =>
        new(
            _processes,
            new ProcessClassifier(
                [GameDir],
                hostPids ?? new HashSet<uint>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            ownImageName: "quiesce");

    [Fact]
    public void AnOrdinaryWindowedAppIsOffered()
    {
        _processes.Add("notepad", @"C:\Program Files\Notepad\notepad.exe");

        var found = Assert.Single(Discovery().Discover(catalog: null).Candidates);

        Assert.Equal("notepad", found.DisplayName);
        Assert.Equal(@"C:\Program Files\Notepad\", found.DirectoryFragment);
        Assert.True(found.CanClose);
        Assert.False(found.IsCovered);
    }

    /// <summary>
    /// Grouping is by directory because that is what an application is. A Chromium-style app runs a dozen
    /// processes side by side, and offering twelve rows for one program would be unusable — and would
    /// invite the user to add the main process and leave the helpers running.
    /// </summary>
    [Fact]
    public void ProcessesInOneDirectoryBecomeOneCandidate()
    {
        const string dir = @"C:\Users\x\AppData\Local\Perplexity\Comet\Application";
        for (var i = 0; i < 20; i++)
        {
            _processes.Add("comet", $@"{dir}\comet.exe", hasWindow: i == 0);
        }

        var found = Assert.Single(Discovery().Discover(catalog: null).Candidates);

        Assert.Equal(20, found.ProcessCount);
        Assert.Equal(1, found.WindowedCount);
        Assert.Equal(["comet"], found.ImageNames);
    }

    /// <summary>
    /// One entry has to cover every executable in the tree. An Electron app runs its helpers under
    /// different names beside the main process, and those helpers are most of what makes it expensive.
    /// </summary>
    [Fact]
    public void DifferentImageNamesInOneDirectoryAreAllListed()
    {
        const string dir = @"C:\Program Files\Thing";
        _processes.Add("thing", $@"{dir}\thing.exe");
        _processes.Add("thing-helper", $@"{dir}\thing-helper.exe", hasWindow: false);

        var found = Assert.Single(Discovery().Discover(catalog: null).Candidates);

        Assert.Equal(["thing", "thing-helper"], found.ImageNames);
    }

    [Theory]
    [InlineData("explorer", @"C:\Windows\explorer.exe")]      // the shell, never touched
    [InlineData("csrss", @"C:\Windows\System32\csrss.exe")]   // system critical
    [InlineData("dwm", @"C:\Windows\System32\dwm.exe")]       // the compositor
    public void ProtectedProcessesAreNeverOffered(string image, string path)
    {
        _processes.Add(image, path);

        Assert.Empty(Discovery().Discover(catalog: null).Candidates);
    }

    [Fact]
    public void AGameIsNeverOffered()
    {
        _processes.Add("doom", $@"{GameDir}\doom.exe");

        Assert.Empty(Discovery().Discover(catalog: null).Candidates);
    }

    [Fact]
    public void ALauncherIsNeverOffered()
    {
        _processes.Add("steam", @"C:\Program Files (x86)\Steam\steam.exe");

        Assert.Empty(Discovery().Discover(catalog: null).Candidates);
    }

    /// <summary>
    /// A service host reaches around every service guardrail, all of which are keyed on service names and
    /// cannot see a request aimed at a PID.
    /// </summary>
    [Fact]
    public void AServiceHostIsNeverOffered()
    {
        var host = _processes.Add("someservice", @"C:\Program Files\Thing\someservice.exe");

        Assert.Empty(Discovery(new HashSet<uint> { (uint)host.Identity.Pid }).Discover(catalog: null).Candidates);
    }

    /// <summary>
    /// Targeting is path-based, so a process whose path cannot be read has no identity to pin and is not
    /// offered — the same rule the classifier applies, seen from the discovery end.
    /// </summary>
    [Fact]
    public void AProcessWithNoReadablePathIsNeverOffered()
    {
        _processes.Add("mystery", imagePath: null);

        Assert.Empty(Discovery().Discover(catalog: null).Candidates);
    }

    [Fact]
    public void QuiescesOwnProcessIsNeverOffered()
    {
        _processes.Add("self", @"C:\Program Files\Quiesce\quiesce.exe", pid: Environment.ProcessId);

        Assert.Empty(Discovery().Discover(catalog: null).Candidates);
    }

    /// <summary>
    /// Another user's session cannot be acted on from here, so offering it would be offering something
    /// Quiesce cannot do.
    /// </summary>
    [Fact]
    public void ProcessesInAnotherSessionAreNotOffered()
    {
        _processes.Add("self", @"C:\Program Files\Quiesce\quiesce.exe", pid: Environment.ProcessId, sessionId: 1);
        _processes.Add("mine", @"C:\Program Files\Mine\mine.exe", sessionId: 1);
        _processes.Add("theirs", @"C:\Program Files\Theirs\theirs.exe", sessionId: 2);

        var found = Discovery().Discover(catalog: null).Candidates;

        Assert.Equal(["mine"], found.Select(c => c.DisplayName));
    }

    /// <summary>
    /// A windowless-only group cannot be closed — WM_CLOSE needs a window and Quiesce has no forceful
    /// option. Listed anyway, because it can still be throttled, and because an empty list is
    /// indistinguishable from a list that was never built.
    /// </summary>
    [Fact]
    public void AWindowlessGroupIsListedButNotClosable()
    {
        _processes.Add("daemon", @"C:\Program Files\Daemon\daemon.exe", hasWindow: false);

        var found = Assert.Single(Discovery().Discover(catalog: null).Candidates);

        Assert.False(found.CanClose);
        Assert.Equal(0, found.WindowedCount);
    }

    [Fact]
    public void AnAppTheCatalogAlreadyCoversIsMarked()
    {
        _processes.Add("comet", @"C:\Users\x\AppData\Local\Perplexity\Comet\Application\comet.exe");

        var catalog = EngineTestHarness.CatalogOf(new CatalogEntry
        {
            Id = "apps.close-browsers",
            Category = "apps",
            Title = "Close browsers",
            Evidence = Evidence.Measured,
            Impact = Impact.High,
            RiskTier = 1,
            Scope = TweakScope.Session,
            RequiresAdmin = false,
            RequiresReboot = false,
            Ops =
            [
                new ProcessOpSpec
                {
                    Action = ProcessAction.Close,
                    ImageName = "comet",
                    UnderDirectories = [@"\Perplexity\Comet\Application\"],
                },
            ],
            WhatItBreaks = "browsers close",
        });

        var found = Assert.Single(Discovery().Discover(catalog).Candidates);

        Assert.True(found.IsCovered);
        Assert.Equal(["apps.close-browsers"], found.CoveredBy);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Thing", @"C:\Program Files\Thing\")]
    [InlineData(@"C:\Program Files\Thing\", @"C:\Program Files\Thing\")]
    [InlineData(@"D:\games\tools", @"D:\games\tools\")]
    public void FragmentsAreRootedAndSeparatorTerminated(string directory, string expected) =>
        Assert.Equal(expected, RunningAppDiscovery.ToDirectoryFragment(directory));

    /// <summary>
    /// A UNC path has no anchor a substring match could rely on: stripping <c>\\server\share</c> leaves a
    /// fragment that begins mid-path, which is exactly the kind that matches somewhere nobody intended.
    /// </summary>
    [Theory]
    [InlineData(@"\\fileserver\apps\thing")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnpinnableDirectoriesProduceNoFragment(string directory) =>
        Assert.Null(RunningAppDiscovery.ToDirectoryFragment(directory));

    [Fact]
    public void AnAppOnAUncShareIsNotOffered()
    {
        _processes.Add("thing", @"\\fileserver\apps\thing\thing.exe");

        Assert.Empty(Discovery().Discover(catalog: null).Candidates);
    }

    /// <summary>
    /// THE ONE MEASURED ON REAL HARDWARE. Grouping by directory assumes a directory belongs to one
    /// application. C:\Windows\System32 held eleven unrelated processes in the first live run, and offering
    /// them as one "app" would have pinned System32 and asked all eleven to close — including rdpclip.exe,
    /// the clipboard of the Remote Desktop session driving the machine.
    /// </summary>
    [Fact]
    public void ADirectoryInsideWindowsIsNeverOfferedAndIsCounted()
    {
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        _processes.Add("ApplicationFrameHost", Path.Combine(system32, "ApplicationFrameHost.exe"));
        _processes.Add("rdpclip", Path.Combine(system32, "rdpclip.exe"));
        _processes.Add("ctfmon", Path.Combine(system32, "ctfmon.exe"), hasWindow: false);

        var result = Discovery().Discover(catalog: null);

        Assert.Empty(result.Candidates);

        // Counted, not silently dropped. A list that quietly shortens reads as "this is everything".
        Assert.Equal(1, result.WindowsComponentsOmitted);
    }

    /// <summary>
    /// WindowsApps stays eligible: each package has its own directory, so the one-directory-one-application
    /// assumption actually holds there. Prefix-matched with a separator rather than by substring, or
    /// "Windows" would swallow "WindowsApps" too.
    /// </summary>
    [Fact]
    public void AStorePackagedAppIsStillOffered()
    {
        _processes.Add(
            "iCloudDrive",
            @"C:\Program Files\WindowsApps\AppleInc.iCloud_15.8.127.0_x64__nzyj5cx40ttqa\iCloud\iCloudDrive.exe");

        var found = Assert.Single(Discovery().Discover(catalog: null).Candidates);

        Assert.Equal("iCloudDrive", found.DisplayName);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Something", false)]
    [InlineData(@"C:\Program Files\Thing", false)]
    [InlineData("", false)]
    public void NonWindowsDirectoriesAreNotTreatedAsSystem(string directory, bool expected) =>
        Assert.Equal(expected, RunningAppDiscovery.IsUnderWindowsDirectory(directory));

    [Fact]
    public void TheWindowsDirectoryAndItsChildrenAreTreatedAsSystem()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(RunningAppDiscovery.IsUnderWindowsDirectory(windows));
        Assert.True(RunningAppDiscovery.IsUnderWindowsDirectory(windows + @"\"));
        Assert.True(RunningAppDiscovery.IsUnderWindowsDirectory(Path.Combine(windows, "System32", "oobe")));
    }

    /// <summary>
    /// Another copy of Quiesce is a different image path, so the classifier's path-based self-protection
    /// does not cover it — and the list has no business offering the user the program they are reading it in.
    /// </summary>
    [Fact]
    public void AnotherCopyOfQuiesceIsNotOffered()
    {
        _processes.Add("quiesce", @"C:\Users\x\AppData\Local\Temp\quiesce-run\quiesce.exe");

        Assert.Empty(Discovery().Discover(catalog: null).Candidates);
    }
}
