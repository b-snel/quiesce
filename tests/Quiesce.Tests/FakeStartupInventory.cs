using Quiesce.Core.Startup;

namespace Quiesce.Tests;

/// <summary>
/// In-memory <see cref="IStartupInventory"/>. Exists to make the awkward shapes arrangeable: an entry
/// with no approval value at all, a blob of an unexpected length, and a logon task that nothing can act on.
/// </summary>
public sealed class FakeStartupInventory : IStartupInventory
{
    private readonly List<StartupItem> _items = [];

    public StartupItem Add(
        string name,
        StartupLocation location = StartupLocation.UserRun,
        byte[]? approval = null,
        string command = "C:\\Program Files\\Thing\\thing.exe")
    {
        var item = new StartupItem
        {
            Name = name,
            Command = command,
            Location = location,
            ApprovalBlob = approval,
        };

        _items.Add(item);
        return item;
    }

    /// <summary>Adds an entry carrying the blob Explorer writes for "enabled".</summary>
    public StartupItem AddEnabled(string name, StartupLocation location = StartupLocation.UserRun) =>
        Add(name, location, [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>Adds an entry the user already switched off by hand, timestamp and all.</summary>
    public StartupItem AddDisabled(string name, StartupLocation location = StartupLocation.UserRun) =>
        Add(name, location, [3, 0, 0, 0, 0x2C, 0xBC, 0x5E, 0xB8, 0xD2, 0xC2, 0xDC, 0x01]);

    public IReadOnlyList<StartupItem> Read(StartupLocation location) =>
        _items.Where(i => i.Location == location).ToList();
}
