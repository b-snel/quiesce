namespace Quiesce.Core.Platform;

/// <summary>
/// Registers a logon scheduled task so Quiesce starts at sign-in without a UAC prompt.
/// </summary>
/// <remarks>
/// <para>
/// A SCHEDULED TASK AND NOT A Run VALUE, and the reason is not a preference. The app requests
/// <c>requireAdministrator</c> for the whole process, so a <c>Run</c> value would raise a consent prompt at
/// every single sign-in — which no one keeps enabled. A task registered to run with the highest available
/// privileges is the only mechanism that does not.
/// </para>
/// <para>
/// THIS IS A STANDING CHANGE TO THE MACHINE AND IT IS NOT JOURNALLED. Everything else Quiesce writes goes
/// through the write-ahead journal and comes back exactly on Restore; this does not, because it is not part
/// of a session — it exists so that a session can be started in the first place. Restore does not remove it.
/// The switch that created it is the only thing that removes it, and the Settings page says so in those
/// words. That asymmetry is deliberate and is worth resisting the urge to "fix": journalling it would mean a
/// Restore could leave the user unable to reach the tool that does the restoring.
/// </para>
/// <para>
/// It is the FIRST scheduled task this product creates. The README describes a boot/logon recovery task as
/// recovery net 3, and there is no code anywhere that registers one — <c>TaskScheduler</c> has been a
/// referenced-and-unused package since M0. This class is not that task and does not pretend to be; it does
/// make writing one considerably cheaper.
/// </para>
/// <para>
/// Because the task appears in the machine's own auto-start surface, Quiesce will see it in its own sign-in
/// list — as a logon task, which it cannot switch off. The Apps and startup page filters it by name and says
/// that it did, rather than offering the user a row about Quiesce that no button can act on.
/// </para>
/// </remarks>
public interface ILogonTaskRegistration
{
    /// <summary>The task's path in the scheduler, shown to the user verbatim.</summary>
    string TaskPath { get; }

    /// <summary>
    /// Whether the task exists right now. The SCHEDULER is the authority, never the settings file.
    /// </summary>
    /// <remarks>
    /// Asked live so the page can notice that the user removed the task in Task Scheduler and report the
    /// truth, instead of trusting a stored bool and silently re-creating something they deleted.
    /// </remarks>
    bool IsRegistered();

    /// <summary>Creates or replaces the task. Returns what to show the user.</summary>
    string Register(string executablePath);

    /// <summary>Removes the task. Returns what to show the user, including "there was none".</summary>
    string Unregister();
}

/// <summary>The real one, over the dahall <c>TaskScheduler</c> wrapper around ITaskService.</summary>
public sealed class LogonTaskRegistration : ILogonTaskRegistration
{
    /// <summary>
    /// A folder of its own, so the task is identifiable and removable by hand.
    /// </summary>
    /// <remarks>
    /// Under a named folder rather than loose in the root: a user auditing their own machine's auto-start
    /// should find one obvious place, and an uninstaller should be able to delete a folder rather than guess
    /// at names.
    /// </remarks>
    private const string FolderName = "Quiesce";

    private const string TaskName = "Start at sign-in";

    public string TaskPath => $@"\{FolderName}\{TaskName}";

    public bool IsRegistered()
    {
        try
        {
            using var service = new Microsoft.Win32.TaskScheduler.TaskService();
            return service.GetTask(TaskPath) is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                      or System.IO.FileNotFoundException)
        {
            // Reported as "not registered" rather than thrown. The page's job here is to render a switch, and
            // a scheduler that cannot be queried is not a reason to refuse to draw the window - the Register
            // call will surface the real error if the user acts.
            return false;
        }
    }

    /// <summary>
    /// Why this executable must not be written into a sign-in task, or null when it is fine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static and public so the refusal is one expression, testable without a scheduler, and reusable by any
    /// caller that would rather grey a switch out than let it throw.
    /// </para>
    /// <para>
    /// THIS EXISTS BECAUSE IT HAPPENED. The Settings page passes <c>Environment.ProcessPath</c>, which is
    /// exactly right for an installed build and exactly wrong for a development one: the launcher stages each
    /// build into <c>%TEMP%\quiesce-run\&lt;timestamp&gt;\</c> and prunes the previous stage on the next run, so
    /// the task was registered against a directory that a later build deleted. It then failed at sign-in with
    /// task result 0x80070002 or, once the app crashed for an unrelated reason, 0xE0434352 — neither of which
    /// says "the path is gone". A task that cannot possibly work is worse than a switch that says why.
    /// </para>
    /// <para>
    /// The temp check is a real prefix test rather than a substring one, and both ends are normalised, for the
    /// same reason <c>ProcessOpSpec.UnderDirectories</c> does it: a folder merely NAMED like the temp path is
    /// not inside it.
    /// </para>
    /// </remarks>
    public static string? UnsuitableExecutableReason(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string full;
        try
        {
            full = Path.GetFullPath(executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"\"{executablePath}\" is not a usable path ({ex.Message}), so it cannot be registered.";
        }

        if (IsUnder(full, Path.GetTempPath()))
        {
            return "Quiesce is running from a temporary staging directory — " +
                   $"\"{full}\". A sign-in task pointing there would break as soon as that directory is " +
                   "cleaned up, which the dev launcher does on its very next run, and it would fail at " +
                   "sign-in with a scheduler error code rather than anything explanatory. Start at sign-in " +
                   "needs Quiesce installed somewhere permanent. Nothing has been registered.";
        }

        // Deliberately last, and phrased as "could not be found" rather than "does not exist": File.Exists
        // reports false for "not permitted to look" too, which is the trap five other places in this
        // codebase document. Either way it is not a path worth writing into a sign-in task.
        if (!File.Exists(full))
        {
            return $"\"{full}\" could not be found, so a sign-in task pointing at it would fail every time. " +
                   "Nothing has been registered.";
        }

        return null;
    }

    private static bool IsUnder(string candidate, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public string Register(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        // Refused rather than registered-and-broken. InvalidOperationException specifically, because the
        // Settings page already catches that, puts the switch back where it was, and shows the message —
        // a switch left on over a task that cannot run is the page claiming something untrue.
        if (UnsuitableExecutableReason(executablePath) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        using var service = new Microsoft.Win32.TaskScheduler.TaskService();
        var definition = service.NewTask();

        definition.RegistrationInfo.Description =
            "Starts Quiesce at sign-in with administrator rights, so its notification-area icon and its " +
            "sync check are available without a consent prompt. Created by Quiesce's Settings page; " +
            "removing it there removes this task.";
        definition.RegistrationInfo.Author = "Quiesce";

        // The whole reason this is a task rather than a Run value. Without it Windows raises a consent
        // prompt at every sign-in, because the app's manifest requests requireAdministrator.
        definition.Principal.RunLevel = Microsoft.Win32.TaskScheduler.TaskRunLevel.Highest;
        definition.Principal.LogonType = Microsoft.Win32.TaskScheduler.TaskLogonType.InteractiveToken;
        definition.Principal.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

        // No time limit: this launches an interactive application the user keeps open for a gaming session,
        // and the default three days would kill it mid-session.
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.StartWhenAvailable = false;

        // One instance. The app has its own single-instance mutex, but a task that queued a second launch
        // would produce a UAC-less process that immediately exits, which looks like a failure to start.
        definition.Settings.MultipleInstances = Microsoft.Win32.TaskScheduler.TaskInstancesPolicy.IgnoreNew;

        definition.Triggers.Add(new Microsoft.Win32.TaskScheduler.LogonTrigger
        {
            UserId = definition.Principal.UserId,
        });

        definition.Actions.Add(new Microsoft.Win32.TaskScheduler.ExecAction($"\"{executablePath}\""));

        var folder = service.RootFolder.CreateFolder(FolderName, exceptionOnExists: false);
        folder.RegisterTaskDefinition(TaskName, definition);

        return $"Quiesce will start when you sign in. It registered the scheduled task {TaskPath}, which " +
               "runs with administrator rights so you are not asked for permission every time. This is a " +
               "standing change to your machine and Restore does NOT remove it — this switch is the only " +
               "thing that does.";
    }

    public string Unregister()
    {
        using var service = new Microsoft.Win32.TaskScheduler.TaskService();

        if (service.GetTask(TaskPath) is null)
        {
            return $"There was no {TaskPath} task to remove. Quiesce does not start itself at sign-in.";
        }

        var folder = service.GetFolder($@"\{FolderName}");
        folder.DeleteTask(TaskName, exceptionOnNotExists: false);

        // The folder goes too when it is empty, so an audit of the machine's scheduled tasks does not show a
        // Quiesce folder implying something is still there.
        try
        {
            if (folder.Tasks.Count == 0 && folder.SubFolders.Count == 0)
            {
                service.RootFolder.DeleteFolder(FolderName, exceptionOnNotExists: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // The task is gone, which is what was asked for. An empty folder left behind is untidy, not wrong.
        }

        return $"Removed {TaskPath}. Quiesce will not start itself at sign-in. Anything it is currently " +
               "holding is unaffected — only Restore un-engages a machine.";
    }
}
