namespace Quiesce.Core.Startup;

/// <summary>
/// Resolves a sign-in entry's command line to the executable it actually launches.
/// </summary>
/// <remarks>
/// Pure, and null rather than a guess. It exists so a running application can be joined to the sign-in entry
/// that starts it — and every naive version of this is wrong on real data measured on this machine:
/// <list type="bullet">
/// <item><c>"…\Discord\Update.exe" --processStart Discord.exe</c> — quoted, with arguments.</item>
/// <item><c>C:\Program Files\Docker\Docker\Docker Desktop.exe</c> — UNQUOTED, with spaces, no arguments.
/// "first whitespace token" turns this into <c>C:\Program</c>.</item>
/// <item><c>"\"C:\…\claude.exe\" --startup"</c> — doubly quoted with escaped quotes.</item>
/// <item><c>…\CometUpdater\145.2.7632.4583\updater.exe --wake</c> — resolves perfectly, and is the
/// UPDATER rather than the browser. Resolving is not the same as being the right program; that is the
/// join's problem, not this method's.</item>
/// </list>
/// </remarks>
public static class StartupCommand
{
    /// <summary>
    /// The executable a Run command line names, or null when it cannot be resolved to a file that EXISTS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three probes, in the order <c>CreateProcess</c> itself uses, and null if none of them lands on a real
    /// file. Every probe is confirmed against the filesystem rather than accepted on shape, because showing
    /// the wrong program's name — or switching off the wrong sign-in entry — is the app being confidently
    /// wrong, which is worse than a blank.
    /// </para>
    /// <para>
    /// The whole-string probe comes FIRST, and it is the one that is easy to leave out: an unquoted path with
    /// spaces and no arguments is a complete, valid, extremely common command line, and it is
    /// indistinguishable from "a program plus arguments" without asking the filesystem.
    /// </para>
    /// <para>
    /// <c>File.Exists</c> is used deliberately here, against the standing warning about it. Its false-on-denied
    /// behaviour is correct for this question: a path this process cannot see is one it must not claim to have
    /// resolved. The warning applies to the data root, where false means "not permitted" and the caller wanted
    /// "absent"; here both answers mean the same thing.
    /// </para>
    /// </remarks>
    public static string? TryResolveExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();

        // Strip one layer of enclosing quotes with escaped inner quotes - the Claude shape,
        // "\"C:\...\claude.exe\" --startup", which arrives from the registry already containing backslash
        // escapes rather than as a quoted path.
        if (trimmed.StartsWith("\"\\\"", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..].Replace("\\\"", "\"", StringComparison.Ordinal);

            if (trimmed.EndsWith('"'))
            {
                trimmed = trimmed[..^1];
            }
        }

        // 1. The whole thing, as-is.
        if (Exists(trimmed, out var whole))
        {
            return whole;
        }

        // 2. A quoted first token.
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing > 1 && Exists(trimmed[1..closing], out var quoted))
            {
                return quoted;
            }

            // A command line that opens a quote and never closes it names nothing findable.
            return null;
        }

        // 3. Progressively longer whitespace-delimited prefixes, shortest first, so
        //    "C:\Program Files\Docker\Docker\Docker Desktop.exe --flag" finds the exe and not the flag.
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = 1; take <= parts.Length; take++)
        {
            if (Exists(string.Join(' ', parts[..take]), out var prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>
    /// Expands environment variables, then asks the filesystem.
    /// </summary>
    /// <remarks>
    /// Expansion first, because <c>%ProgramFiles%\…</c> and <c>%LOCALAPPDATA%\…</c> are both real shapes in a
    /// Run key, and a literal percent sign in a path that does not expand simply will not exist — which is
    /// the answer this method should give anyway.
    /// </remarks>
    private static bool Exists(string candidate, out string? resolved)
    {
        resolved = null;

        var trimmed = candidate.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return false;
        }

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(trimmed);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // Only ever an executable. A Run value pointing at a .dll through rundll32 names rundll32, and
        // claiming the dll would be claiming something that is not a process image.
        if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !expanded.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (!File.Exists(expanded))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        resolved = expanded;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="ancestorOrSame"/> is the install directory, or a parent of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE JOIN PREDICATE, and ancestor rather than equality for a measured reason: install directories are
    /// VERSIONED LEAVES and Run values point at the stable parent stub. On this machine Discord runs from
    /// <c>…\Discord\app-1.0.9249</c> while its Run value launches <c>…\Discord\Update.exe</c>, so directory
    /// equality finds nothing and ancestry finds it correctly.
    /// </para>
    /// <para>
    /// NEVER A SHARED-ANCESTOR TEST, which is the tempting relaxation and is wrong. Comet runs from
    /// <c>…\Perplexity\Comet\Application</c> and its Run value launches
    /// <c>…\Perplexity\CometUpdater\145.2.7632.4583\updater.exe</c>. Those share <c>…\Perplexity\</c>, so a
    /// common-ancestor rule would tie the browser row to its updater's entry and switching it off would stop
    /// Comet updating rather than stop Comet starting. Ancestry rejects it, which is the whole point.
    /// </para>
    /// <para>
    /// Separator-normalised and compared with a trailing separator on both sides, the same both-ends rule
    /// <c>ProcessOpSpec.UnderDirectories</c> uses so that <c>\Discord\</c> cannot match
    /// <c>\DiscordCanary\</c>.
    /// </para>
    /// </remarks>
    public static bool IsAncestorOrSame(string? ancestorOrSame, string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(ancestorOrSame) || string.IsNullOrWhiteSpace(installDirectory))
        {
            return false;
        }

        var a = WithTrailingSeparator(ancestorOrSame);
        var b = WithTrailingSeparator(installDirectory);

        return b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithTrailingSeparator(string path)
    {
        var normalised = path.Replace('/', '\\').TrimEnd('\\');
        return normalised + '\\';
    }
}
