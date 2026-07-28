using Quiesce.Core.Startup;

namespace Quiesce.Tests;

/// <summary>
/// Resolving a sign-in entry to the executable it launches, and joining it to a running application.
/// </summary>
/// <remarks>
/// Every command line below was READ OFF THIS MACHINE rather than invented, because each one defeats a
/// different plausible implementation. The resolution tests use a real file the test creates, since the
/// method deliberately confirms against the filesystem — a shape-only parser would happily "resolve" a path
/// to a program that is not there.
/// </remarks>
public class StartupCommandTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));

    public StartupCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Creates a real file so the resolver has something to confirm against.</summary>
    private string Exe(string relative)
    {
        var full = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "not really an executable");
        return full;
    }

    [Fact]
    public void An_unquoted_path_with_spaces_and_no_arguments_resolves_whole()
    {
        // MEASURED: Docker Desktop's HKCU Run value is exactly this shape -
        //   C:\Program Files\Docker\Docker\Docker Desktop.exe
        // with no quotes and no arguments. "First whitespace token" yields C:\Program, which is the single
        // most likely wrong implementation and the reason the whole-string probe runs FIRST.
        var exe = Exe(@"Program Files\Docker\Docker\Docker Desktop.exe");

        Assert.Equal(exe, StartupCommand.TryResolveExecutable(exe));
    }

    [Fact]
    public void A_quoted_path_with_arguments_resolves_to_the_path()
    {
        // MEASURED: Discord, Steam, OneDrive, NordVPN and six others on this machine.
        var exe = Exe(@"Discord\Update.exe");

        Assert.Equal(exe, StartupCommand.TryResolveExecutable($"\"{exe}\" --processStart Discord.exe"));
    }

    [Fact]
    public void A_doubly_quoted_value_with_escaped_quotes_resolves()
    {
        // MEASURED, and the strangest real shape here. Claude's Run value is stored as
        //   "\"C:\...\claude.exe\" --startup"
        // - an outer pair of quotes wrapping an inner escaped pair. A naive quoted-first-token reader takes
        // everything up to the second quote character and comes back with a single backslash.
        var exe = Exe(@"AnthropicClaude\claude.exe");

        Assert.Equal(exe, StartupCommand.TryResolveExecutable($"\"\\\"{exe}\\\" --startup\""));
    }

    [Fact]
    public void An_unquoted_path_with_spaces_AND_arguments_resolves_by_growing_the_prefix()
    {
        var exe = Exe(@"Program Files\Some Vendor\Some App.exe");

        Assert.Equal(exe, StartupCommand.TryResolveExecutable($"{exe} --flag --another"));
    }

    [Fact]
    public void An_environment_variable_is_expanded()
    {
        // %LOCALAPPDATA%\... and %ProgramFiles%\... are both real Run-value shapes.
        var exe = Exe("Expanded.exe");
        var withVar = exe.Replace(Path.GetTempPath().TrimEnd('\\'), "%TEMP%", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(exe, StartupCommand.TryResolveExecutable(withVar));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"rundll32.exe C:\Windows\System32\something.dll,EntryPoint")]
    [InlineData(@"C:\Does\Not\Exist\anywhere.exe")]
    [InlineData("\"C:\\unterminated quote.exe --flag")]
    public void Anything_that_cannot_be_confirmed_resolves_to_null(string? command)
    {
        // Null rather than a best guess. Every consumer of this either shows the user a program name or
        // decides whether to pre-check a box that switches something off, and being confidently wrong at
        // either is worse than a blank.
        //
        // The rundll32 case matters specifically: the process image really is rundll32, so claiming the .dll
        // would be naming something that is not a process at all.
        Assert.Null(StartupCommand.TryResolveExecutable(command));
    }

    [Fact]
    public void A_file_that_exists_but_is_not_an_executable_is_not_resolved()
    {
        var txt = Exe("readme.txt");

        Assert.Null(StartupCommand.TryResolveExecutable(txt));
    }

    // ------------------------------------------------------------------ the join

    private const string CometInstall = @"C:\Users\t\AppData\Local\Perplexity\Comet\Application";
    private const string CometUpdater = @"C:\Users\t\AppData\Local\Perplexity\CometUpdater\145.2.7632.4583";
    private const string DiscordInstall = @"C:\Users\t\AppData\Local\Discord\app-1.0.9249";
    private const string DiscordStub = @"C:\Users\t\AppData\Local\Discord";

    [Fact]
    public void A_run_value_in_an_ancestor_of_the_install_directory_joins()
    {
        // MEASURED: Discord runs from ...\Discord\app-1.0.9249 and its Run value launches
        // ...\Discord\Update.exe. Install directories are VERSIONED LEAVES and Run values point at the
        // stable parent stub, so directory EQUALITY finds nothing here and ancestry finds it.
        Assert.True(StartupCommand.IsAncestorOrSame(DiscordStub, DiscordInstall));
    }

    [Fact]
    public void The_same_directory_joins()
    {
        // MEASURED: Comet.lnk resolves to ...\Perplexity\Comet\Application\comet.exe, whose directory IS the
        // install directory. This is the case that made resolving .lnk targets worth the COM.
        Assert.True(StartupCommand.IsAncestorOrSame(CometInstall, CometInstall));
    }

    [Fact]
    public void The_updater_in_a_sibling_tree_never_joins()
    {
        // THE FALSE-POSITIVE GUARD, and the reason this is ancestry rather than a shared-ancestor test.
        //
        // MEASURED: Comet's Run value launches ...\Perplexity\CometUpdater\145.2.7632.4583\updater.exe. That
        // shares ...\Perplexity\ with the browser, so a common-ancestor rule would tie the Comet row to its
        // UPDATER's entry - and switching it off would stop Comet updating itself rather than stop Comet
        // starting, while the UI claimed the opposite.
        Assert.False(StartupCommand.IsAncestorOrSame(CometUpdater, CometInstall));

        // And not in the other direction either.
        Assert.False(StartupCommand.IsAncestorOrSame(CometInstall, CometUpdater));
    }

    [Fact]
    public void A_sibling_with_a_shared_name_prefix_never_joins()
    {
        // The both-ends separator rule, the same one ProcessOpSpec.UnderDirectories enforces: \Discord\ must
        // not match \DiscordCanary\.
        Assert.False(StartupCommand.IsAncestorOrSame(
            @"C:\Users\t\AppData\Local\Discord",
            @"C:\Users\t\AppData\Local\DiscordCanary\app-1.0.0"));
    }

    [Theory]
    [InlineData(null, CometInstall)]
    [InlineData(CometInstall, null)]
    [InlineData("", CometInstall)]
    public void An_unknown_path_on_either_side_never_joins(string? ancestor, string? install)
    {
        // "Cannot say" resolves to no join, never to a guess - the same rule ProcessPrior.IsSameProgram
        // follows for an unreadable image path.
        Assert.False(StartupCommand.IsAncestorOrSame(ancestor, install));
    }

    [Fact]
    public void Trailing_separators_and_slash_direction_do_not_change_the_answer()
    {
        Assert.True(StartupCommand.IsAncestorOrSame(DiscordStub + @"\", DiscordInstall));
        Assert.True(StartupCommand.IsAncestorOrSame(DiscordStub.Replace('\\', '/'), DiscordInstall));
        Assert.True(StartupCommand.IsAncestorOrSame(DiscordStub.ToUpperInvariant(), DiscordInstall));
    }
}
