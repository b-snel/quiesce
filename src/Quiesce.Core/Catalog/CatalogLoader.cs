using System.Text.Json;
using Microsoft.Win32;

namespace Quiesce.Core.Catalog;

/// <summary>Loads and validates a catalog file. Any validation failure refuses the whole file.</summary>
/// <remarks>
/// Validation lives here — against the exact types the engine deserializes to — rather than in an
/// external JSON Schema that could drift from the code. CI runs these checks via the test suite.
/// </remarks>
public static class CatalogLoader
{
    /// <summary>The one catalog schema version this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CatalogFile LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, Path.GetFileName(path));
    }

    public static CatalogFile Load(Stream stream, string sourceName)
    {
        // Probe schemaVersion with a cheap document parse before full deserialization, and refuse
        // hard on anything newer than this build understands: best-effort parsing of a future
        // schema is how a tool silently mis-writes a machine.
        using var buffered = new MemoryStream();
        stream.CopyTo(buffered);
        buffered.Position = 0;

        using (var doc = JsonDocument.Parse(buffered, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
        {
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number)
            {
                throw new CatalogException($"{sourceName}: missing or non-numeric schemaVersion.");
            }

            var version = sv.GetInt32();
            if (version != SupportedSchemaVersion)
            {
                throw new CatalogException(
                    $"{sourceName}: schemaVersion {version} is not supported by this build " +
                    $"(supported: {SupportedSchemaVersion}). Refusing rather than guessing.");
            }
        }

        buffered.Position = 0;

        CatalogFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CatalogFile>(buffered, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Surface every catalog problem as one exception type. Callers catch CatalogException;
            // letting a raw JsonException escape means a malformed catalog crashes the CLI with a
            // stack trace instead of printing what is wrong with the file.
            throw new CatalogException($"{sourceName}: malformed catalog — {ex.Message}");
        }

        if (file is null)
        {
            throw new CatalogException($"{sourceName}: deserialized to null.");
        }

        Validate(file, sourceName);
        return file;
    }

    public static void Validate(CatalogFile file, string sourceName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in file.Entries)
        {
            void Fail(string message) =>
                throw new CatalogException($"{sourceName}: entry '{entry.Id}': {message}");

            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                Fail("empty id.");
            }

            if (!seen.Add(entry.Id))
            {
                Fail("duplicate id.");
            }

            if (entry.RiskTier < 1)
            {
                Fail("riskTier must be >= 1; tier 0 is reserved for guardrail-locked rows.");
            }

            if (entry.Ops.Count == 0)
            {
                Fail("no ops.");
            }

            if (string.IsNullOrWhiteSpace(entry.WhatItBreaks))
            {
                Fail("whatItBreaks is required. 'Nothing' must be said explicitly.");
            }

            ValidateProcessEntryShape(entry, Fail);

            foreach (var op in entry.Ops)
            {
                if (op is ServiceOpSpec serviceOp)
                {
                    ValidateService(serviceOp, entry, Fail);
                    continue;
                }

                if (op is ProcessOpSpec processOp)
                {
                    ValidateProcess(processOp, Fail);
                    continue;
                }

                if (op is not RegistryOpSpec registryOp)
                {
                    Fail($"op kind '{op.GetType().Name}' is not supported by this build.");
                    continue;
                }

                ValidateRegistry(registryOp, entry, sourceName, Fail);
            }
        }
    }

    /// <summary>
    /// Entry-level rules for process ops, which constrain the whole entry rather than one op.
    /// </summary>
    /// <remarks>
    /// The entry is the transaction unit, and a close cannot participate in a rollback — nothing
    /// unwinds "the application exited". So an entry that mixes a close with anything reversible could
    /// not honour the "never half-applied" guarantee, and the guarantee is worth more than the
    /// flexibility. Refused at load, where it is a catalog bug with a message, rather than discovered
    /// at apply time as a half-undone entry.
    /// </remarks>
    private static void ValidateProcessEntryShape(CatalogEntry entry, Action<string> Fail)
    {
        var processOps = entry.Ops.OfType<ProcessOpSpec>().ToList();
        if (processOps.Count == 0)
        {
            return;
        }

        if (processOps.Count != entry.Ops.Count)
        {
            Fail("mixes process ops with registry or service ops. A close cannot be rolled back, so an " +
                 "entry containing process ops must contain only process ops.");
        }

        if (processOps.Select(op => op.Action).Distinct().Count() > 1)
        {
            Fail("mixes close and throttle ops. A failed throttle rolls its entry back and a close " +
                 "cannot, so one entry does one or the other.");
        }

        // A close is undone by the user reopening the application, and a priority class does not
        // survive the process exiting - neither is a standing preference, and boot recovery deliberately
        // leaves persistent-scoped steps alone.
        if (entry.Scope != TweakScope.Session)
        {
            Fail($"process ops must be Session scope, not {entry.Scope}: neither a closed application nor " +
                 "a priority class survives a reboot, so there is nothing for a persistent scope to mean.");
        }

        // Claiming admin rights this entry does not need would make the UI gate it, and gate it
        // permanently for an unelevated user who could have run it perfectly well.
        if (entry.RequiresAdmin)
        {
            Fail("declares requiresAdmin: true, but closing or throttling a process in your own session " +
                 "needs no elevation.");
        }
    }

    private static void ValidateProcess(ProcessOpSpec op, Action<string> Fail)
    {
        if (string.IsNullOrWhiteSpace(op.ImageName))
        {
            Fail("process op has no imageName.");
            return;
        }

        if (op.ImageName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '*', '?']) >= 0)
        {
            Fail($"imageName '{op.ImageName}' must be a bare image name - paths and wildcards belong in " +
                 "underDirectories, which is what actually establishes identity.");
        }

        // Data narrows, never widens. A catalog - shipped by anyone, including a future me - must not
        // be able to talk Quiesce into closing the shell, a system-critical process, the compositor, or
        // the WebView host that other applications' windows live in.
        if (Guardrails.IsProcessProtected(op.ImageName))
        {
            Fail($"process '{op.ImageName}' is on the never-touch list and cannot appear in a catalog.");
        }

        if (op.UnderDirectories.Count == 0)
        {
            Fail($"process op '{op.ImageName}' names no directories. Targeting is path-based: an image " +
                 "name on its own would match a copy of the program anywhere on disk.");
        }

        foreach (var directory in op.UnderDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                Fail($"process op '{op.ImageName}' has an empty directory fragment.");
                continue;
            }

            // Separator-delimited at both ends, so the fragment names a directory rather than a prefix.
            // Without the trailing separator, "\Discord" would also match "\DiscordCanary\", and a
            // one-character fragment would match every path on the machine.
            if (!directory.StartsWith('\\') || !directory.EndsWith('\\'))
            {
                Fail($"directory fragment '{directory}' must start and end with a backslash, so that it " +
                     "names a directory and cannot match a longer name that merely starts the same way.");
            }

            if (directory.Trim('\\').Length == 0)
            {
                Fail($"directory fragment '{directory}' names nothing and would match every path.");
            }

            if (directory.Contains("..", StringComparison.Ordinal)
                || directory.IndexOfAny(['*', '?']) >= 0)
            {
                Fail($"directory fragment '{directory}' must be a literal path fragment: no wildcards, no '..'.");
            }
        }

        switch (op.Action)
        {
            case ProcessAction.Throttle when op.ThrottleTo is null:
                Fail($"throttle op '{op.ImageName}' does not say what to throttle to.");
                break;

            case ProcessAction.Close when op.ThrottleTo is not null:
                Fail($"close op '{op.ImageName}' carries throttleTo '{op.ThrottleTo}'; a close sets no priority.");
                break;

            default:
                break;
        }
    }

    private static void ValidateService(ServiceOpSpec op, CatalogEntry entry, Action<string> Fail)
    {
        if (string.IsNullOrWhiteSpace(op.Service))
        {
            Fail("service name is required.");
        }

        // Service configuration is machine-wide, so an entry claiming otherwise would let the UI
        // offer it to an unelevated user who could only ever watch it fail.
        if (!entry.RequiresAdmin)
        {
            Fail($"service op '{op.Service}' requires admin, but the entry declares requiresAdmin: false.");
        }

        // Data can only ever narrow the guardrail. A catalog - shipped by anyone, including a
        // future me - must not be able to talk Quiesce into reconfiguring a tier-0 service.
        if (Guardrails.IsServiceProtected(op.Service))
        {
            Fail($"service '{op.Service}' is on the never-touch list and cannot appear in a catalog.");
        }

        if (op.StartMode == ServiceStartMode.Automatic && op.StopNow)
        {
            Fail($"service '{op.Service}' asks to stop while staying Automatic; it would restart at next boot.");
        }
    }

    private static void ValidateRegistry(RegistryOpSpec op, CatalogEntry entry, string sourceName, Action<string> Fail)
    {
                if (op.View != "Registry64")
                {
                    Fail($"op view '{op.View}' — only Registry64 is allowed. WOW6432Node redirection is how writes silently miss.");
                }

                if (string.IsNullOrWhiteSpace(op.Subkey) || op.Subkey.StartsWith('\\') || op.Subkey.EndsWith('\\'))
                {
                    Fail($"malformed subkey '{op.Subkey}'.");
                }

                if (string.IsNullOrWhiteSpace(op.Value))
                {
                    // Default-value writes are not supported: their absent-vs-empty semantics are
                    // ambiguous, which breaks faithful restore.
                    Fail("value name is required.");
                }

                var kind = ParseKind(op.ExpectedKind) ?? throw new CatalogException(
                    $"{sourceName}: entry '{entry.Id}': unknown expectedKind '{op.ExpectedKind}'.");

                if (!DataMatchesKind(op.LeanData, kind))
                {
                    // A DWord write to a REG_SZ target silently no-ops. Catching the mismatch at
                    // catalog load is the difference between a real tool and a placebo.
                    Fail($"leanData does not match expectedKind {op.ExpectedKind}.");
                }

                // "requiresAdmin iff HKLM" is the obvious rule and it is wrong: the per-user policy
                // subtree is owned by Administrators and grants the interactive user read-only, so
                // an unelevated write there is denied. Declaring requiresAdmin honestly is what
                // lets the UI warn instead of failing at apply time.
                if (op.Hive == CatalogHive.HKCU
                    && op.Subkey.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase)
                    && !entry.RequiresAdmin)
                {
                    Fail($"op targets the admin-owned per-user policy subtree '{op.Subkey}' but requiresAdmin is false.");
                }
    }

    public static RegistryValueKind? ParseKind(string kind) => kind switch
    {
        "DWord" => RegistryValueKind.DWord,
        "QWord" => RegistryValueKind.QWord,
        "String" => RegistryValueKind.String,
        "ExpandString" => RegistryValueKind.ExpandString,
        "MultiString" => RegistryValueKind.MultiString,
        "Binary" => RegistryValueKind.Binary,
        _ => null,
    };

    private static bool DataMatchesKind(JsonElement data, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord or RegistryValueKind.QWord => data.ValueKind == JsonValueKind.Number,
        RegistryValueKind.String or RegistryValueKind.ExpandString => data.ValueKind == JsonValueKind.String,
        RegistryValueKind.MultiString => data.ValueKind == JsonValueKind.Array,
        RegistryValueKind.Binary => data.ValueKind == JsonValueKind.String,
        _ => false,
    };
}

public sealed class CatalogException(string message) : Exception(message);
