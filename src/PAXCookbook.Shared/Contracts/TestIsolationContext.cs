using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component contract for TEST ISOLATION.
//
// This is the SINGLE authoritative isolation context shared by the App, the
// headless daemon, the attached window, the Setup bootstrapper, the installed
// Setup DLL, the self-handoff process, the apply-update process, update
// relaunch, helper runners, provider-native-test, and Setup Repair. It exists
// so that an isolated (test / pilot-validation) run can NEVER resolve, write to,
// launch, update, or discover the real per-user PAX Cookbook installation.
//
// THIS FILE IS THE SINGLE SOURCE OF TRUTH. It is compiled into PAXCookbook.Shared
// (used by the Setup installer) AND linked directly into the native host
// (PAXCookbook.App, which does not reference Shared) so both use the exact same
// schema, validation rules, and immutable runtime object with no duplicated
// logic that can drift. It depends only on System + System.Text.Json so it can
// be linked into the App without pulling in the rest of Shared.
//
// Doctrine (binding):
//   - The context is a STRUCTURED, SCHEMA-VERSIONED descriptor. Every root is an
//     absolute path that must be a descendant of ONE declared isolated root, and
//     that isolated root must be wholly disjoint from the real install / real
//     workspace / profile shell locations.
//   - Validation FAILS CLOSED. Missing, partial, malformed, duplicate, or
//     unknown fields, non-absolute paths, paths that escape the isolated root,
//     reparse-point (symlink/junction) escapes, or any overlap with the real
//     installation are REJECTED. There is no fallback to a real default root.
//   - Honoring the context at runtime is additionally BUILD-GATED behind the
//     PAXCOOKBOOK_TEST_ISOLATION compile symbol (defined only by an explicit
//     /p:TestIsolation=true build). Stable/customer builds contain no activation
//     path, so no descriptor can ever place a customer build into isolated mode.
//   - No registration / tenant / account / secret identifiers are ever carried
//     here or on any command line derived from it.

// The strict, closed set of descriptor fields. Anything else is rejected.
public sealed record TestIsolationDescriptor
{
    public int? SchemaVersion { get; init; }

    // The one declared isolated root. Every other path must live under it.
    public string? IsolatedRoot { get; init; }

    // Absolute roots, each a descendant of (or equal to) IsolatedRoot.
    public string? InstallRoot { get; init; }
    public string? LocalStateRoot { get; init; }
    public string? Workspace { get; init; }
    public string? WebView2Data { get; init; }
    public string? EngineState { get; init; }
    public string? PayloadCache { get; init; }
    public string? Logs { get; init; }
    public string? CoordinationState { get; init; }
    public string? SetupTempRoot { get; init; }

    // Behavior gates. All required (fail closed if omitted).
    public bool? ShellIntegrationEnabled { get; init; }
    public bool? UpdateChecksEnabled { get; init; }
    public bool? NetworkPayloadDownloadEnabled { get; init; }
    public bool? UpdateApplyEnabled { get; init; }
    public bool? PreserveIsolationOnRelaunch { get; init; }
    public bool? InstalledProductDiscoveryEnabled { get; init; }

    // Optional, but REQUIRED (and validated) when UpdateApplyEnabled is true:
    // an explicit local test payload that lives inside the isolated root, plus
    // its expected SHA-256. When UpdateApplyEnabled is false these must be null.
    public string? TestLocalPayloadPath { get; init; }
    public string? TestLocalPayloadSha256 { get; init; }
}

// The immutable, fully-validated runtime object. Construction is only possible
// through TestIsolationParser, so an instance is proof that every rule passed.
public sealed class TestIsolationContext
{
    internal TestIsolationContext(
        string isolatedRoot, string installRoot, string localStateRoot, string workspace,
        string webView2Data, string engineState, string payloadCache, string logs,
        string coordinationState, string setupTempRoot,
        bool shellIntegrationEnabled, bool updateChecksEnabled,
        bool networkPayloadDownloadEnabled, bool updateApplyEnabled,
        bool preserveIsolationOnRelaunch, bool installedProductDiscoveryEnabled,
        string? testLocalPayloadPath, string? testLocalPayloadSha256)
    {
        IsolatedRoot = isolatedRoot;
        InstallRoot = installRoot;
        LocalStateRoot = localStateRoot;
        Workspace = workspace;
        WebView2Data = webView2Data;
        EngineState = engineState;
        PayloadCache = payloadCache;
        Logs = logs;
        CoordinationState = coordinationState;
        SetupTempRoot = setupTempRoot;
        ShellIntegrationEnabled = shellIntegrationEnabled;
        UpdateChecksEnabled = updateChecksEnabled;
        NetworkPayloadDownloadEnabled = networkPayloadDownloadEnabled;
        UpdateApplyEnabled = updateApplyEnabled;
        PreserveIsolationOnRelaunch = preserveIsolationOnRelaunch;
        InstalledProductDiscoveryEnabled = installedProductDiscoveryEnabled;
        TestLocalPayloadPath = testLocalPayloadPath;
        TestLocalPayloadSha256 = testLocalPayloadSha256;
    }

    public string IsolatedRoot { get; }
    public string InstallRoot { get; }
    public string LocalStateRoot { get; }
    public string Workspace { get; }
    public string WebView2Data { get; }
    public string EngineState { get; }
    public string PayloadCache { get; }
    public string Logs { get; }
    public string CoordinationState { get; }
    public string SetupTempRoot { get; }

    public bool ShellIntegrationEnabled { get; }
    public bool UpdateChecksEnabled { get; }
    public bool NetworkPayloadDownloadEnabled { get; }
    public bool UpdateApplyEnabled { get; }
    public bool PreserveIsolationOnRelaunch { get; }
    public bool InstalledProductDiscoveryEnabled { get; }

    public string? TestLocalPayloadPath { get; }
    public string? TestLocalPayloadSha256 { get; }

    // True when the given absolute path resolves to a location at or under the
    // isolated root. Used by process-discovery / stop guards to prove a
    // candidate belongs to the isolated run and never the real installation.
    public bool ContainsPath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;
        string full;
        try { full = Path.GetFullPath(absolutePath); }
        catch { return false; }
        return TestIsolationPaths.IsAtOrUnder(full, IsolatedRoot);
    }
}

public sealed record TestIsolationParseResult(
    bool Ok, TestIsolationContext? Context, IReadOnlyList<string> Errors)
{
    public static TestIsolationParseResult Success(TestIsolationContext ctx) =>
        new(true, ctx, Array.Empty<string>());

    public static TestIsolationParseResult Fail(IReadOnlyList<string> errors) =>
        new(false, null, errors);

    public static TestIsolationParseResult Fail(string error) =>
        new(false, null, new[] { error });
}

// Shared path helpers used by the validator and by the runtime object.
public static class TestIsolationPaths
{
    // Canonical real per-user install root the isolation MUST NOT touch.
    // Computed here (rather than via AppPaths) so this file stays self-contained
    // and linkable into the App. Used ONLY as a rejection comparator — never as
    // a resolution fallback.
    public static string RealInstallRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PAXCookbook");

    // Profile shell locations an isolated root must never overlap.
    public static IEnumerable<string> ProfileShellRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    public static bool IsAtOrUnder(string candidateFull, string boundaryFull)
    {
        if (string.Equals(candidateFull, boundaryFull, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = boundaryFull.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // True when either path is at-or-under the other (any containment overlap).
    public static bool Overlaps(string aFull, string bFull)
        => IsAtOrUnder(aFull, bFull) || IsAtOrUnder(bFull, aFull);
}

public static class TestIsolationParser
{
    public const int CurrentSchemaVersion = 1;

    // The structured argument that carries the descriptor path between processes.
    // Value is an absolute path to a JSON descriptor that lives inside the
    // isolated root. Never a serialized blob and never a registration id.
    public const string DescriptorArg = "--test-isolation";

    // The exact, closed set of accepted JSON property names. Anything else is a
    // hard rejection (unknown field).
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "isolatedRoot", "installRoot", "localStateRoot", "workspace",
        "webView2Data", "engineState", "payloadCache", "logs", "coordinationState",
        "setupTempRoot", "shellIntegrationEnabled", "updateChecksEnabled",
        "networkPayloadDownloadEnabled", "updateApplyEnabled", "preserveIsolationOnRelaunch",
        "installedProductDiscoveryEnabled", "testLocalPayloadPath", "testLocalPayloadSha256",
    };

    public static TestIsolationParseResult ParseFile(string descriptorPath)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath))
            return TestIsolationParseResult.Fail("descriptor path is empty");
        string json;
        try { json = File.ReadAllText(descriptorPath); }
        catch (Exception ex)
        {
            return TestIsolationParseResult.Fail($"descriptor unreadable: {ex.GetType().Name}");
        }
        return Parse(json);
    }

    public static TestIsolationParseResult Parse(string json)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return TestIsolationParseResult.Fail("descriptor is empty");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            return TestIsolationParseResult.Fail($"descriptor is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var rootEl = doc.RootElement;
            if (rootEl.ValueKind != JsonValueKind.Object)
                return TestIsolationParseResult.Fail("descriptor root must be a JSON object");

            // Reject unknown and duplicate fields (strict, closed schema).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in rootEl.EnumerateObject())
            {
                if (!KnownKeys.Contains(prop.Name))
                    errors.Add($"unknown field: {prop.Name}");
                else if (!seen.Add(prop.Name))
                    errors.Add($"duplicate field: {prop.Name}");
            }
            if (errors.Count > 0) return TestIsolationParseResult.Fail(errors);

            int? schema = ReadInt(rootEl, "schemaVersion", errors);
            if (schema is null) errors.Add("missing required field: schemaVersion");
            else if (schema.Value != CurrentSchemaVersion)
                errors.Add($"unsupported schemaVersion: {schema.Value} (expected {CurrentSchemaVersion})");

            string? isolatedRaw = ReadString(rootEl, "isolatedRoot", errors);
            string? installRaw = ReadString(rootEl, "installRoot", errors);
            string? localStateRaw = ReadString(rootEl, "localStateRoot", errors);
            string? workspaceRaw = ReadString(rootEl, "workspace", errors);
            string? webViewRaw = ReadString(rootEl, "webView2Data", errors);
            string? engineRaw = ReadString(rootEl, "engineState", errors);
            string? payloadRaw = ReadString(rootEl, "payloadCache", errors);
            string? logsRaw = ReadString(rootEl, "logs", errors);
            string? coordRaw = ReadString(rootEl, "coordinationState", errors);
            string? setupTmpRaw = ReadString(rootEl, "setupTempRoot", errors);

            bool? shell = ReadBool(rootEl, "shellIntegrationEnabled", errors, required: true);
            bool? updChk = ReadBool(rootEl, "updateChecksEnabled", errors, required: true);
            bool? net = ReadBool(rootEl, "networkPayloadDownloadEnabled", errors, required: true);
            bool? applyEnabled = ReadBool(rootEl, "updateApplyEnabled", errors, required: true);
            bool? preserve = ReadBool(rootEl, "preserveIsolationOnRelaunch", errors, required: true);
            bool? discovery = ReadBool(rootEl, "installedProductDiscoveryEnabled", errors, required: true);

            string? payloadPathRaw = ReadOptionalString(rootEl, "testLocalPayloadPath", errors);
            string? payloadShaRaw = ReadOptionalString(rootEl, "testLocalPayloadSha256", errors);

            // Required path presence.
            RequirePresent(isolatedRaw, "isolatedRoot", errors);
            RequirePresent(installRaw, "installRoot", errors);
            RequirePresent(localStateRaw, "localStateRoot", errors);
            RequirePresent(workspaceRaw, "workspace", errors);
            RequirePresent(webViewRaw, "webView2Data", errors);
            RequirePresent(engineRaw, "engineState", errors);
            RequirePresent(payloadRaw, "payloadCache", errors);
            RequirePresent(logsRaw, "logs", errors);
            RequirePresent(coordRaw, "coordinationState", errors);
            RequirePresent(setupTmpRaw, "setupTempRoot", errors);

            if (errors.Count > 0) return TestIsolationParseResult.Fail(errors);

            // Canonicalize the isolated root and reject overlaps with reality.
            if (!TryCanonicalAbsolute(isolatedRaw!, "isolatedRoot", errors, out string isolated))
                return TestIsolationParseResult.Fail(errors);

            string realRoot;
            try { realRoot = Path.GetFullPath(TestIsolationPaths.RealInstallRoot()); }
            catch { realRoot = TestIsolationPaths.RealInstallRoot(); }
            if (TestIsolationPaths.Overlaps(isolated, realRoot))
                errors.Add("isolatedRoot overlaps the real install root");
            foreach (var shellRoot in TestIsolationPaths.ProfileShellRoots())
            {
                if (string.IsNullOrEmpty(shellRoot)) continue;
                string sr;
                try { sr = Path.GetFullPath(shellRoot); } catch { continue; }
                if (TestIsolationPaths.Overlaps(isolated, sr))
                {
                    errors.Add("isolatedRoot overlaps a profile shell location");
                    break;
                }
            }

            // The isolated root itself must not be a reparse point (a link could
            // redirect the entire sandbox onto the real tree).
            if (IsReparsePoint(isolated))
                errors.Add("isolatedRoot is a reparse point (symlink/junction)");

            // Each declared sub-root: absolute, at-or-under isolatedRoot, no
            // reparse-point escape between it and the isolated root.
            string install = ValidateSub(installRaw!, "installRoot", isolated, errors);
            string localState = ValidateSub(localStateRaw!, "localStateRoot", isolated, errors);
            string workspace = ValidateSub(workspaceRaw!, "workspace", isolated, errors);
            string webView = ValidateSub(webViewRaw!, "webView2Data", isolated, errors);
            string engine = ValidateSub(engineRaw!, "engineState", isolated, errors);
            string payload = ValidateSub(payloadRaw!, "payloadCache", isolated, errors);
            string logs = ValidateSub(logsRaw!, "logs", isolated, errors);
            string coord = ValidateSub(coordRaw!, "coordinationState", isolated, errors);
            string setupTmp = ValidateSub(setupTmpRaw!, "setupTempRoot", isolated, errors);

            // Update-apply / test-payload consistency.
            string? payloadPath = null;
            string? payloadSha = null;
            if (applyEnabled == true)
            {
                if (string.IsNullOrWhiteSpace(payloadPathRaw))
                    errors.Add("updateApplyEnabled requires testLocalPayloadPath");
                else
                    payloadPath = ValidateSub(payloadPathRaw, "testLocalPayloadPath", isolated, errors);

                if (string.IsNullOrWhiteSpace(payloadShaRaw) || !IsSha256Hex(payloadShaRaw))
                    errors.Add("updateApplyEnabled requires a 64-hex testLocalPayloadSha256");
                else
                    payloadSha = payloadShaRaw;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(payloadPathRaw) || !string.IsNullOrWhiteSpace(payloadShaRaw))
                    errors.Add("testLocalPayload* must be null when updateApplyEnabled is false");
            }

            if (errors.Count > 0) return TestIsolationParseResult.Fail(errors);

            var ctx = new TestIsolationContext(
                isolated, install, localState, workspace, webView, engine, payload, logs,
                coord, setupTmp,
                shell!.Value, updChk!.Value, net!.Value, applyEnabled!.Value,
                preserve!.Value, discovery!.Value,
                payloadPath, payloadSha);
            return TestIsolationParseResult.Success(ctx);
        }
    }

    private static string ValidateSub(string raw, string field, string isolated, List<string> errors)
    {
        if (!TryCanonicalAbsolute(raw, field, errors, out string full))
            return string.Empty;
        if (!TestIsolationPaths.IsAtOrUnder(full, isolated))
        {
            errors.Add($"{field} is not inside the isolated root");
            return full;
        }
        if (HasReparsePointBetween(full, isolated))
            errors.Add($"{field} escapes the isolated root through a reparse point");
        return full;
    }

    private static bool TryCanonicalAbsolute(string raw, string field, List<string> errors, out string full)
    {
        full = string.Empty;
        if (!Path.IsPathFullyQualified(raw))
        {
            errors.Add($"{field} must be an absolute path");
            return false;
        }
        try { full = Path.GetFullPath(raw); }
        catch (Exception ex)
        {
            errors.Add($"{field} is not a valid path: {ex.GetType().Name}");
            return false;
        }
        return true;
    }

    // Walk from the deepest EXISTING ancestor of 'child' up toward 'boundary';
    // if any existing directory in that chain is a reparse point, the child can
    // be redirected outside the isolated root.
    private static bool HasReparsePointBetween(string child, string boundary)
    {
        try
        {
            var dir = new DirectoryInfo(child);
            while (dir is not null)
            {
                string cur = dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
                string b = boundary.TrimEnd(Path.DirectorySeparatorChar);
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
                if (string.Equals(cur, b, StringComparison.OrdinalIgnoreCase))
                    break;
                dir = dir.Parent;
            }
        }
        catch { return true; } // fail closed on any inspection error
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            return di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
    }

    private static void RequirePresent(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"missing required field: {field}");
    }

    private static int? ReadInt(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v))
        {
            errors.Add($"{name} must be an integer");
            return null;
        }
        return v;
    }

    private static string? ReadString(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{name} must be a string");
            return null;
        }
        return el.GetString();
    }

    private static string? ReadOptionalString(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{name} must be a string or null");
            return null;
        }
        return el.GetString();
    }

    private static bool? ReadBool(JsonElement root, string name, List<string> errors, bool required)
    {
        if (!root.TryGetProperty(name, out var el))
        {
            if (required) errors.Add($"missing required field: {name}");
            return null;
        }
        if (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False)
        {
            errors.Add($"{name} must be a boolean");
            return null;
        }
        return el.GetBoolean();
    }

    private static bool IsSha256Hex(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }
}
