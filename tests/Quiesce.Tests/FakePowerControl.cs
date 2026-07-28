using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// An in-memory <see cref="IPowerControl"/>: a set of installed schemes and one active selection.
/// </summary>
/// <remarks>
/// Models the three things that can go wrong on a real machine and that the engine has to handle
/// differently: the target scheme is missing, the active scheme cannot be read, and the set call
/// reports success without changing anything. The last one matters because
/// <c>PowerSetActiveScheme</c> returns a Win32 error code rather than a BOOL, so "did not throw" and
/// "worked" are further apart here than anywhere else in the project.
/// </remarks>
public sealed class FakePowerControl : IPowerControl
{
    private readonly List<PowerScheme> _installed = [];

    public Guid? Active { get; set; }

    /// <summary>When true, <see cref="Query"/> reports the active scheme as unreadable.</summary>
    public bool ActiveUnreadable { get; set; }

    /// <summary>When true, <see cref="SetActiveScheme"/> silently does nothing and reports success.</summary>
    public bool SilentlyIgnoreWrites { get; set; }

    /// <summary>When set, <see cref="SetActiveScheme"/> throws it.</summary>
    public Exception? ThrowOnSet { get; set; }

    public List<Guid> SetCalls { get; } = [];

    public FakePowerControl Install(Guid id, string? name, uint? sleepAfterAcSeconds = 0)
    {
        _installed.Add(new PowerScheme { Id = id, FriendlyName = name, SleepAfterAcSeconds = sleepAfterAcSeconds });
        return this;
    }

    public FakePowerControl Remove(Guid id)
    {
        _installed.RemoveAll(s => s.Id == id);
        return this;
    }

    /// <summary>Balanced (sleeps after 5h, as measured) plus Ultimate Performance (never sleeps).</summary>
    public static FakePowerControl LikeTheDevelopmentMachine()
    {
        var power = new FakePowerControl();
        power.Install(WellKnownPowerSchemes.Balanced, "Balanced", 18000);
        power.Install(WellKnownPowerSchemes.HighPerformance, "High performance", 0);
        power.Install(WellKnownPowerSchemes.PowerSaver, "Power saver", 900);
        power.Install(WellKnownPowerSchemes.UltimatePerformance, "Ultimate Performance", 0);
        power.Active = WellKnownPowerSchemes.Balanced;
        return power;
    }

    public PowerSchemeSnapshot Query()
    {
        var active = ActiveUnreadable ? null : Active;

        return new PowerSchemeSnapshot
        {
            Active = active,
            ActiveFriendlyName = active is { } id ? _installed.FirstOrDefault(s => s.Id == id)?.FriendlyName : null,
            Installed = [.. _installed],
        };
    }

    public void SetActiveScheme(Guid scheme)
    {
        SetCalls.Add(scheme);

        if (ThrowOnSet is { } ex)
        {
            throw ex;
        }

        if (SilentlyIgnoreWrites)
        {
            return;
        }

        Active = scheme;
    }
}
