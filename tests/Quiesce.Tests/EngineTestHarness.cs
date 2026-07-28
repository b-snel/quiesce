using System.Text.Json;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.Tests;

/// <summary>
/// A complete engine environment on a fake registry and a throwaway data root.
/// Dispose deletes the data root.
/// </summary>
public sealed class EngineTestHarness : IDisposable
{
    public const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    public EngineTestHarness()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "quiesce-tests", Guid.NewGuid().ToString("N"));
        Registry = new FakeRegistry();
        Registry.LoadUserHive(Sid);
        Broadcaster = new FakeBroadcaster();
        Paths = new QuiescePaths(DataRoot);
        Engine = new TransactionEngine(Registry, Broadcaster, Paths, new EngineInfo
        {
            AppVersion = "0.0.0-test",
            OsBuild = "10.0.26200",
            UserSid = Sid,
        });
    }

    public string DataRoot { get; }

    public FakeRegistry Registry { get; }

    public FakeBroadcaster Broadcaster { get; }

    public QuiescePaths Paths { get; }

    public TransactionEngine Engine { get; }

    public QuiesceState State => new StateStore(DataRoot).Load();

    public IReadOnlyList<JournalRecord> Journal(Guid sessionId) =>
        JournalReader.Read(Path.Combine(Paths.SessionDir(sessionId), "journal.jsonl")).Records;

    /// <summary>A one-op DWord entry, defaulting to the shape of the real M1 catalog entry.</summary>
    public static CatalogEntry DwordEntry(
        string id = "test.dword",
        string subkey = @"SOFTWARE\QuiesceTest\Target",
        string valueName = "TestValue",
        int leanData = 0,
        TweakScope scope = TweakScope.Persistent,
        IReadOnlyList<ActivationKind>? activation = null)
        => new()
        {
            Id = id,
            Category = "test",
            Title = id,
            Evidence = Evidence.Measured,
            Impact = Impact.Low,
            RiskTier = 1,
            Scope = scope,
            RequiresAdmin = false,
            RequiresReboot = false,
            Ops =
            [
                new RegistryOpSpec
                {
                    Kind = "registry",
                    Hive = CatalogHive.HKCU,
                    Subkey = subkey,
                    Value = valueName,
                    ExpectedKind = "DWord",
                    LeanData = JsonSerializer.SerializeToElement(leanData),
                },
            ],
            Activation = activation ?? [],
            WhatItBreaks = "nothing (test)",
        };

    public static CatalogFile CatalogOf(params CatalogEntry[] entries) => new()
    {
        SchemaVersion = 1,
        CatalogVersion = "test",
        Entries = entries,
    };

    public static RegistryTarget TargetOf(CatalogEntry entry, int opIndex = 0)
    {
        var op = entry.Ops[opIndex];
        return new RegistryTarget
        {
            Hive = "HKU",
            UserSid = Sid,
            Subkey = op.Subkey,
            ValueName = op.Value,
        };
    }

    public static RegistryData Dword(uint value) => new()
    {
        Kind = "DWord",
        Data = JsonSerializer.SerializeToElement(value),
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(DataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Temp dir cleanup is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
