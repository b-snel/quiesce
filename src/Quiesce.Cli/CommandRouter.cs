namespace Quiesce.Cli;

/// <summary>
/// Verb dispatch for <c>quiesce.exe</c>.
/// </summary>
/// <remarks>
/// This binary carries every CLI verb, deliberately. <c>Quiesce.App</c> is a <c>WinExe</c> with
/// <c>requireAdministrator</c>, so it has no attached console and its exit code is not observable by
/// the shell that launched it - putting verbs there would make every CLI-based acceptance test
/// unrunnable by construction. It is also recovery net 3: <c>quiesce revert-all</c> must work with
/// the GUI broken, uninstalled, or never installed, reading only the journal and never the catalog.
/// </remarks>
public static class CommandRouter
{
    /// <summary>Exit codes. Stable - the verification scripts assert on these.</summary>
    public static class ExitCode
    {
        public const int Ok = 0;
        public const int UsageError = 2;
        public const int NotImplemented = 3;
        public const int NotElevated = 4;
    }

    private sealed record Verb(string Name, string Milestone, string Summary);

    private static readonly Verb[] Verbs =
    [
        new("inventory",     "M1", "Print a read-only report of services, processes, and current tweak state."),
        new("print-plan",    "M1", "Show exactly what Engage would do. Changes nothing. This is dry-run."),
        new("engage",        "M1", "Apply the active profile, journalling every change before it is made."),
        new("restore",       "M1", "Revert the current session from its journal."),
        new("revert-all",    "M1", "Revert every incomplete session found on disk. The panic button."),
        new("recover",       "M1", "Finish an interrupted apply or revert. Run automatically at boot/logon."),
        new("verify-revert", "M1", "Apply, immediately revert, and assert the machine is byte-identical."),
        new("discover",      "M5", "Find installed games across Steam, Epic, Battle.net, EA, GOG, Xbox."),
        new("update",        "M7", "Check for a new release. --appcast <url> overrides the source (testing only)."),
    ];

    public static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelpFlag(args[0]))
        {
            PrintHelp();
            return ExitCode.Ok;
        }

        if (args[0] is "--version" or "-v")
        {
            Console.WriteLine(VersionInfo.Informational);
            return ExitCode.Ok;
        }

        var verb = Verbs.FirstOrDefault(v => v.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (verb is null)
        {
            Console.Error.WriteLine($"quiesce: unknown command '{args[0]}'.");
            Console.Error.WriteLine("Run 'quiesce --help' for the list of commands.");
            return ExitCode.UsageError;
        }

        Console.Error.WriteLine($"quiesce: '{verb.Name}' is not implemented yet (planned for {verb.Milestone}).");
        return ExitCode.NotImplemented;
    }

    private static bool IsHelpFlag(string arg) =>
        arg is "--help" or "-h" or "-?" or "/?" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine($"quiesce {VersionInfo.Informational} - a true Windows game mode");
        Console.WriteLine();
        Console.WriteLine("  Quiets non-gaming apps, services and Windows bloat for a session, then restores");
        Console.WriteLine("  your machine exactly as it was. Every change is individually reversible.");
        Console.WriteLine();
        Console.WriteLine("  Expect better 1% lows and less stutter. Do not expect higher average FPS.");
        Console.WriteLine();
        Console.WriteLine("USAGE");
        Console.WriteLine("  quiesce <command> [options]");
        Console.WriteLine();
        Console.WriteLine("COMMANDS");

        var width = Verbs.Max(v => v.Name.Length);
        foreach (var v in Verbs)
        {
            Console.WriteLine($"  {v.Name.PadRight(width)}  {v.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine("OPTIONS");
        Console.WriteLine("  -h, --help     Show this help.");
        Console.WriteLine("  -v, --version  Show the version.");
        Console.WriteLine();
        Console.WriteLine("EXIT CODES");
        Console.WriteLine($"  {ExitCode.Ok}  success");
        Console.WriteLine($"  {ExitCode.UsageError}  usage error");
        Console.WriteLine($"  {ExitCode.NotImplemented}  command not implemented yet");
        Console.WriteLine($"  {ExitCode.NotElevated}  administrator rights required");
        Console.WriteLine();
        Console.WriteLine("  https://github.com/b-snel/quiesce");
    }
}
