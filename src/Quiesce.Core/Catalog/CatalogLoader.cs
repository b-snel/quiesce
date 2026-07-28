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

            foreach (var op in entry.Ops)
            {
                if (op is ServiceOpSpec serviceOp)
                {
                    ValidateService(serviceOp, entry, Fail);
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
