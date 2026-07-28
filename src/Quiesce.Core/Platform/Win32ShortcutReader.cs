using System.Runtime.InteropServices;
using System.Text;

namespace Quiesce.Core.Platform;

/// <summary>Reads the target path stored in a <c>.lnk</c> shortcut.</summary>
public interface IShortcutReader
{
    /// <summary>The stored target, or null when it cannot be read. Never a guess.</summary>
    string? TryReadTarget(string shortcutPath);
}

/// <summary>
/// <c>IShellLinkW</c>, with every resolution behaviour switched off.
/// </summary>
/// <remarks>
/// <para>
/// THIS REVERSES A DELIBERATE DECISION AND THE REVERSAL IS DOCUMENTED WHERE THE CODE IS.
/// <c>Win32StartupInventory</c> says of the same files: "The shortcut's target is deliberately NOT resolved.
/// Doing it needs COM… and the target adds nothing Quiesce acts on: the approval value is keyed on the FILE
/// NAME, so the file name is the identity." That reasoning is still exactly right for switching an entry OFF.
/// It is not right for JOINING a sign-in entry to a running application, which is what this is for — and on
/// this machine the only way Comet starts is a Startup-folder shortcut, so without this the user's own example
/// application can never be matched to the thing that launches it.
/// </para>
/// <para>
/// THE FIVE FLAGS ARE THE WHOLE SAFETY ARGUMENT. A <c>.lnk</c> is a binary file in a directory a user can
/// write, parsed inside a process running as Administrator — the CVE-2010-2568 / CVE-2017-8464 family. What
/// made those exploitable was the shell's RESOLUTION behaviour: chasing moved targets, consulting distributed
/// link tracking, reading link info, touching the network, and loading icon handlers to draw a preview.
/// <c>SLR_NO_UI | SLR_NOUPDATE | SLR_NOSEARCH | SLR_NOTRACK | SLR_NOLINKINFO</c> disables all of it, so this
/// reads the target string the file already contains and lets the shell chase nothing.
/// </para>
/// <para>
/// The residual risk is the structured parse of the file itself, and it is accepted knowingly rather than
/// hidden: the alternative is writing our own MS-SHLLINK parser, which is strictly worse, and the only files
/// read are the ones already enumerated from the two Startup folders. Any failure is null — an unreadable
/// shortcut is offered to the user by name and unchecked, never guessed at.
/// </para>
/// <para>
/// Interop is a hand-written declaration, matching every other interop in this tree. Despite the
/// <c>Microsoft.Windows.CsWin32</c> package reference there is no <c>NativeMethods.txt</c> anywhere in the
/// repository, so CsWin32 is not this codebase's convention whatever the package list implies.
/// </para>
/// </remarks>
public sealed class Win32ShortcutReader : IShortcutReader
{
    /// <summary>No dialogs, ever. This runs behind a page render, not behind a user gesture.</summary>
    private const int SlrNoUi = 0x0001;

    /// <summary>Do not search for a target that has moved.</summary>
    private const int SlrNoSearch = 0x0010;

    /// <summary>Do not consult distributed link tracking, which can reach the network.</summary>
    private const int SlrNoTrack = 0x0020;

    /// <summary>Do not read the link-info block, which can reference volumes and shares.</summary>
    private const int SlrNoLinkInfo = 0x0040;

    /// <summary>Do not write the file back. Reading must never mutate the user's shortcut.</summary>
    private const int SlrNoUpdate = 0x0008;

    private const int ResolveFlags = SlrNoUi | SlrNoUpdate | SlrNoSearch | SlrNoTrack | SlrNoLinkInfo;

    /// <summary>Matches <c>MAX_PATH</c>, which is what <c>IShellLinkW.GetPath</c> writes into.</summary>
    private const int MaxPath = 260;

    public string? TryReadTarget(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return null;
        }

        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellLinkClsid);
            if (type is null)
            {
                return null;
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IShellLinkW link)
            {
                return null;
            }

            ((IPersistFile)link).Load(shortcutPath, StgmRead);

            // Resolve with everything switched off. Skipping it entirely also works for a shortcut whose
            // target still exists, but calling it with only the no-op flags set is the documented way to say
            // "give me what is stored and do nothing else" - and it keeps the flags visible at the call site
            // rather than implied by their absence.
            link.Resolve(IntPtr.Zero, ResolveFlags);

            var buffer = new StringBuilder(MaxPath);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);

            var target = buffer.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException
                                      or UnauthorizedAccessException or IOException
                                      or MissingMethodException or TypeLoadException)
        {
            // Null, and the caller offers the entry by name and unchecked. A shortcut Quiesce cannot read is
            // one it cannot prove anything about.
            return null;
        }
        finally
        {
            // Released deterministically rather than left to the finalizer. This runs once per shortcut during
            // a page render, and an RCW held until GC would keep a shell object alive inside an elevated
            // process for no reason.
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    /// <summary>The <c>ShellLink</c> coclass.</summary>
    /// <remarks>
    /// Created through its CLSID rather than declared as a <c>[ComImport]</c> coclass type: the coclass form
    /// needs the class to be declared as implementing the interface before C# will allow the cast, and going
    /// via the CLSID keeps one type in this file instead of two and makes the release in the <c>finally</c>
    /// unambiguous.
    /// </remarks>
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    private const int StgmRead = 0x00000000;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxPath,
            IntPtr findData,
            int flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxArgs);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int icon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);

        void Resolve(IntPtr hwnd, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
