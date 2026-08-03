using Quiesce.Core.Platform;
using Xunit;

namespace Quiesce.Tests;

/// <summary>
/// That a sign-in task is never registered against a path that cannot survive to the next sign-in.
/// </summary>
/// <remarks>
/// Pure, and it touches no scheduler: the decision is a string predicate, so it can be tested without
/// registering anything on the machine running the tests. Which matters, because the alternative — finding
/// out by hand — is what happened. The Settings page passed <c>Environment.ProcessPath</c>, the dev launcher
/// stages each build into <c>%TEMP%\quiesce-run\&lt;timestamp&gt;\</c> and prunes the previous stage, so the
/// registered task pointed into a directory the next build deleted.
/// </remarks>
public class LogonTaskPathTests
{
    [Fact]
    public void A_staged_build_under_temp_is_refused_and_says_why()
    {
        var staged = Path.Combine(Path.GetTempPath(), "quiesce-run", "20260729-095211", "Quiesce.exe");

        var reason = LogonTaskRegistration.UnsuitableExecutableReason(staged);

        Assert.NotNull(reason);
        Assert.Contains("temporary staging directory", reason, StringComparison.Ordinal);
        // It must say nothing was done. A refusal the user reads as a partial success is the failure this
        // whole guard exists to avoid.
        Assert.Contains("Nothing has been registered", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_executable_is_refused()
    {
        var missing = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "no-such-dir", "Quiesce.exe");

        var reason = LogonTaskRegistration.UnsuitableExecutableReason(missing);

        Assert.NotNull(reason);
        Assert.Contains("could not be found", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_executable_outside_temp_is_accepted()
    {
        // The test host's own executable: a real file, definitely not under %TEMP%, and it costs nothing to
        // look at. Without a positive case the two refusals above would pass just as well if the method
        // refused everything.
        var real = Environment.ProcessPath;
        Assert.NotNull(real);

        Assert.Null(LogonTaskRegistration.UnsuitableExecutableReason(real));
    }

    [Fact]
    public void A_directory_merely_named_like_the_temp_path_is_not_treated_as_inside_it()
    {
        // The prefix test has to be separator-delimited. A substring check would refuse this, and refusing a
        // legitimate install directory because its name starts with the same letters is the mirror image of
        // the bug: a switch that cannot be turned on, with a reason that makes no sense.
        var lookalike = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + "-installed";
        var candidate = Path.Combine(lookalike, "Quiesce.exe");

        var reason = LogonTaskRegistration.UnsuitableExecutableReason(candidate);

        // It is still refused - the file does not exist - but for the RIGHT reason, which is what pins the
        // prefix rule rather than the outcome.
        Assert.NotNull(reason);
        Assert.DoesNotContain("temporary staging directory", reason, StringComparison.Ordinal);
        Assert.Contains("could not be found", reason, StringComparison.Ordinal);
    }
}
