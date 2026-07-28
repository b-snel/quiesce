using System.Reflection;

namespace Quiesce.Cli;

/// <summary>
/// Reads the MinVer-stamped version off the assembly.
/// </summary>
/// <remarks>
/// MinVer writes <see cref="AssemblyInformationalVersionAttribute"/> at build time from the git tag
/// (plus commit height). It does not generate a <c>ThisAssembly</c> class — that is
/// Nerdbank.GitVersioning — so the attribute is read directly.
/// </remarks>
internal static class VersionInfo
{
    /// <summary>The full semver string, e.g. <c>1.2.0-beta.1</c>, or <c>unknown</c>.</summary>
    public static string Informational { get; } =
        typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "unknown";
}
