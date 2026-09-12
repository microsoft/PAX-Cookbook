using System;
using System.Collections.Generic;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component contract for MACHINE-POLICY DETECTION.
//
// PAX Cookbook detects an administrator-authored, machine-wide policy that
// describes three independent dimensions:
//   * desktopAccess     — self_service | organization_managed
//   * managedChefKeys    — disabled | enabled
//   * windowsService     — disabled | enabled
//
// DETECTION IS DESCRIPTIVE ONLY. It reads and classifies policy; it never
// executes, installs, enables, mutates, or provisions anything. An `enabled`
// dimension means only "future behavior is policy-authorized", never activation.
//
// Authoritative source (production): the machine-wide key
//   HKLM\SOFTWARE\Policies\PAXCookbook
// read-only, least privilege. That subtree is the conventional Windows Group
// Policy boundary: its default ACL grants write only to Administrators / SYSTEM
// and read to Users, and it is GPO/MDM-deployable. A normal (non-elevated) user
// cannot create, weaken, replace, or remove values under it (binding
// Constraints 16/18: the ordinary desktop process may READ validated machine
// policy but may NOT write it, and stays non-elevated). There is NO HKCU
// fallback, NO environment-variable override, NO command-line override in
// production, and NO file/network/tenant source, so nothing user-controlled can
// override the machine policy.
//
// THIS FILE IS THE SINGLE SOURCE OF TRUTH. It is compiled into PAXCookbook.Shared
// (used by the Setup installer) AND linked directly into the native host
// (PAXCookbook.App, which does not reference Shared) via the established
// <Compile Include=... Link=...> pattern, so both use the exact same registry
// sub-path, value names, enum strings, schema version, bounds, and validation
// with no duplicated constants that can drift. It depends only on System +
// System.Collections.Generic (no Win32 registry types) so it links cleanly into
// the App and compiles under Shared's plain net8.0 target; the production reader
// translates the concrete registry value kind into the portable value-kind below.
//
// Fail-closed doctrine (binding):
//   * Absence (key missing, or present with none of the canonical values) is the
//     benign default: desktopAccess = self_service, both capabilities disabled.
//   * Present-but-malformed / partial / unsupported-schema / wrong-type /
//     oversized / unknown-enum / conflicting policy is NEVER modeled as a valid
//     three-enum object. It classifies as `invalid` with a bounded reason, and —
//     the critical asymmetry — desktopAccess = undetermined (NEVER self_service),
//     both capabilities disabled. Uncertainty is represented explicitly; it is
//     never coerced to the permissive enum, so malformed policy cannot grant
//     self-service authority.
//   * A source that is not the trusted machine-policy subtree classifies as
//     `untrusted` (undetermined, both disabled). It is producible only via an
//     injected source; the production reader is trusted by construction.
//   * `enabled` capability flags are produced ONLY when state == configured.
//   * The parser is pure: no side effects, and it NEVER throws registry/parse
//     exceptions through the product boundary. It NEVER places registry content
//     (paths, raw values) in ToString, logs, HTTP, IPC, or UI — only the bounded
//     `state` classification and bounded `recoveryReason` may be logged.

// Canonical constants: the single source of truth for the registry location,
// value names, accepted schema version, closed enum strings, and bounds.
public static class MachinePolicyContract
{
    // Read-only machine-policy key path under HKEY_LOCAL_MACHINE. Hardcoded and
    // NEVER configurable from user/runtime input.
    public const string PolicySubKeyPath = @"SOFTWARE\Policies\PAXCookbook";

    // Canonical value names directly under the key.
    public const string ValuePolicySchemaVersion = "PolicySchemaVersion"; // REG_DWORD
    public const string ValueDesktopAccess = "DesktopAccess";             // REG_SZ
    public const string ValueManagedChefKeys = "ManagedChefKeys";         // REG_SZ
    public const string ValueWindowsService = "WindowsService";           // REG_SZ

    // Only this schema version is accepted.
    public const int SupportedSchemaVersion = 1;

    // Closed enum strings for the REG_SZ dimensions.
    public const string DesktopAccessSelfService = "self_service";
    public const string DesktopAccessOrganizationManaged = "organization_managed";
    public const string CapabilityDisabled = "disabled";
    public const string CapabilityEnabled = "enabled";

    // Bounded maximum encoded length (characters) for a REG_SZ value; anything
    // longer is rejected as oversized BEFORE its content is parsed.
    public const int MaxRegSzLength = 64;

    // The canonical value names, in stable order.
    public static readonly IReadOnlyList<string> CanonicalValueNames = new[]
    {
        ValuePolicySchemaVersion,
        ValueDesktopAccess,
        ValueManagedChefKeys,
        ValueWindowsService,
    };
}

// Portable registry value-kind. Deliberately NOT the concrete Win32 registry
// value-kind type so this contract carries no Win32 dependency into Shared. The
// production reader maps DWORD -> Dword, SZ -> String, everything else -> Other.
public enum MachinePolicyValueKind
{
    Dword,
    String,
    Other,
}

// The outcome of accessing the machine-policy source.
public enum MachinePolicySourceAccess
{
    Absent,
    Present,
    AccessDenied,
    Untrusted,
}

// desktopAccess dimension. Undetermined is the fail-closed value used whenever
// policy is invalid/untrusted — it is NEVER coerced to SelfService.
public enum MachinePolicyDesktopAccess
{
    SelfService,
    OrganizationManaged,
    Undetermined,
}

// A managed capability dimension.
public enum MachinePolicyCapability
{
    Disabled,
    Enabled,
}

// The classified state of the machine policy.
public enum MachinePolicyState
{
    NotConfigured,
    Configured,
    Invalid,
    Untrusted,
}

// The bounded reason a policy is not a clean configured/not-configured result.
public enum MachinePolicyRecoveryReason
{
    None,
    Malformed,
    Partial,
    UnsupportedSchema,
    AccessDenied,
    TypeMismatch,
    Oversized,
    UnknownEnum,
    Conflicting,
    UntrustedSource,
}

// A single bounded raw value read from the source. For a REG_SZ, StringValue is
// the content (or null when it was rejected as oversized, so oversized content
// never enters memory) and EncodedLength is its character length. For a REG_DWORD,
// DwordValue carries the integer.
public sealed class MachinePolicyRawValue
{
    public MachinePolicyRawValue(
        MachinePolicyValueKind kind,
        string? stringValue,
        int? dwordValue,
        int encodedLength)
    {
        Kind = kind;
        StringValue = stringValue;
        DwordValue = dwordValue;
        EncodedLength = encodedLength;
    }

    public MachinePolicyValueKind Kind { get; }

    public string? StringValue { get; }

    public int? DwordValue { get; }

    public int EncodedLength { get; }

    public static MachinePolicyRawValue ForString(string value)
    {
        int len = value?.Length ?? 0;
        // Do not carry oversized content into memory; keep only the length.
        string? stored = len > MachinePolicyContract.MaxRegSzLength ? null : value;
        return new MachinePolicyRawValue(MachinePolicyValueKind.String, stored, dwordValue: null, encodedLength: len);
    }

    public static MachinePolicyRawValue ForDword(int value)
        => new(MachinePolicyValueKind.Dword, stringValue: null, dwordValue: value, encodedLength: 4);

    public static MachinePolicyRawValue ForOther(int encodedLength)
        => new(MachinePolicyValueKind.Other, stringValue: null, dwordValue: null, encodedLength: encodedLength);
}

// An immutable, bounded snapshot of the machine-policy source. Value names are
// compared case-insensitively (registry semantics).
public sealed class MachinePolicySnapshot
{
    private static readonly IReadOnlyDictionary<string, MachinePolicyRawValue> Empty =
        new Dictionary<string, MachinePolicyRawValue>(StringComparer.OrdinalIgnoreCase);

    public MachinePolicySnapshot(
        MachinePolicySourceAccess access,
        IReadOnlyDictionary<string, MachinePolicyRawValue>? values)
    {
        Access = access;
        if (values is null || values.Count == 0)
        {
            Values = Empty;
        }
        else
        {
            var copy = new Dictionary<string, MachinePolicyRawValue>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, MachinePolicyRawValue> pair in values)
            {
                if (!string.IsNullOrEmpty(pair.Key) && pair.Value is not null)
                {
                    copy[pair.Key] = pair.Value;
                }
            }
            Values = copy;
        }
    }

    public MachinePolicySourceAccess Access { get; }

    public IReadOnlyDictionary<string, MachinePolicyRawValue> Values { get; }

    public static MachinePolicySnapshot Absent()
        => new(MachinePolicySourceAccess.Absent, values: null);

    public static MachinePolicySnapshot AccessDenied()
        => new(MachinePolicySourceAccess.AccessDenied, values: null);

    public static MachinePolicySnapshot Untrusted()
        => new(MachinePolicySourceAccess.Untrusted, values: null);

    public static MachinePolicySnapshot Present(IReadOnlyDictionary<string, MachinePolicyRawValue> values)
        => new(MachinePolicySourceAccess.Present, values);
}

// A source of the raw machine-policy snapshot. Production reads HKLM read-only;
// tests inject an in-memory implementation. Production code NEVER accepts a test
// seam from user-controlled runtime input.
public interface IMachinePolicySource
{
    MachinePolicySnapshot Read();
}

// The discriminated detection result. Impossible states are unrepresentable:
// enabled capabilities are producible ONLY through the organization-managed
// configured factory, and desktopAccess = Undetermined is producible ONLY through
// the invalid/untrusted factories. Malformed policy can never be a valid
// three-enum object.
public sealed class MachinePolicyDetection
{
    private MachinePolicyDetection(
        MachinePolicyState state,
        MachinePolicyDesktopAccess desktopAccess,
        MachinePolicyCapability managedChefKeys,
        MachinePolicyCapability windowsService,
        MachinePolicyRecoveryReason recoveryReason)
    {
        State = state;
        DesktopAccess = desktopAccess;
        ManagedChefKeys = managedChefKeys;
        WindowsService = windowsService;
        RecoveryReason = recoveryReason;
    }

    public MachinePolicyState State { get; }

    public MachinePolicyDesktopAccess DesktopAccess { get; }

    public MachinePolicyCapability ManagedChefKeys { get; }

    public MachinePolicyCapability WindowsService { get; }

    public MachinePolicyRecoveryReason RecoveryReason { get; }

    // Absent / present-with-no-canonical-values: benign personal default.
    public static MachinePolicyDetection NotConfigured()
        => new(
            MachinePolicyState.NotConfigured,
            MachinePolicyDesktopAccess.SelfService,
            MachinePolicyCapability.Disabled,
            MachinePolicyCapability.Disabled,
            MachinePolicyRecoveryReason.None);

    // Valid self-service policy. Capabilities are forced disabled — a
    // self-service device can never carry an enabled managed capability.
    public static MachinePolicyDetection ConfiguredSelfService()
        => new(
            MachinePolicyState.Configured,
            MachinePolicyDesktopAccess.SelfService,
            MachinePolicyCapability.Disabled,
            MachinePolicyCapability.Disabled,
            MachinePolicyRecoveryReason.None);

    // Valid organization-managed policy. The ONLY way an enabled capability is
    // ever produced.
    public static MachinePolicyDetection ConfiguredOrganizationManaged(
        MachinePolicyCapability managedChefKeys,
        MachinePolicyCapability windowsService)
        => new(
            MachinePolicyState.Configured,
            MachinePolicyDesktopAccess.OrganizationManaged,
            managedChefKeys,
            windowsService,
            MachinePolicyRecoveryReason.None);

    // Fail-closed invalid policy: desktopAccess = Undetermined, both caps disabled.
    public static MachinePolicyDetection Invalid(MachinePolicyRecoveryReason reason)
        => new(
            MachinePolicyState.Invalid,
            MachinePolicyDesktopAccess.Undetermined,
            MachinePolicyCapability.Disabled,
            MachinePolicyCapability.Disabled,
            reason == MachinePolicyRecoveryReason.None ? MachinePolicyRecoveryReason.Malformed : reason);

    // Fail-closed untrusted source: desktopAccess = Undetermined, both caps disabled.
    public static MachinePolicyDetection Untrusted()
        => new(
            MachinePolicyState.Untrusted,
            MachinePolicyDesktopAccess.Undetermined,
            MachinePolicyCapability.Disabled,
            MachinePolicyCapability.Disabled,
            MachinePolicyRecoveryReason.UntrustedSource);

    // Bounded, content-free. Never includes registry paths or raw value content.
    public override string ToString()
        => $"MachinePolicyDetection[state={State}, desktopAccess={DesktopAccess}, reason={RecoveryReason}]";
}

// The pure, deterministic, side-effect-free classifier. It takes a materialized
// snapshot and never touches the registry, the session provider, the network,
// the filesystem, or any capability. It never throws through the boundary.
public static class MachinePolicyParser
{
    public static MachinePolicyDetection Classify(MachinePolicySnapshot snapshot)
    {
        if (snapshot is null)
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Malformed);
        }

        switch (snapshot.Access)
        {
            case MachinePolicySourceAccess.AccessDenied:
                return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.AccessDenied);
            case MachinePolicySourceAccess.Untrusted:
                return MachinePolicyDetection.Untrusted();
            case MachinePolicySourceAccess.Absent:
                return MachinePolicyDetection.NotConfigured();
            case MachinePolicySourceAccess.Present:
                break;
            default:
                return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Malformed);
        }

        IReadOnlyDictionary<string, MachinePolicyRawValue> values = snapshot.Values;

        bool hasSchema = values.TryGetValue(MachinePolicyContract.ValuePolicySchemaVersion, out MachinePolicyRawValue? schema);
        bool hasDesktop = values.TryGetValue(MachinePolicyContract.ValueDesktopAccess, out MachinePolicyRawValue? desktop);
        bool hasChef = values.TryGetValue(MachinePolicyContract.ValueManagedChefKeys, out MachinePolicyRawValue? chef);
        bool hasService = values.TryGetValue(MachinePolicyContract.ValueWindowsService, out MachinePolicyRawValue? service);

        int canonicalCount = (hasSchema ? 1 : 0) + (hasDesktop ? 1 : 0) + (hasChef ? 1 : 0) + (hasService ? 1 : 0);

        // Present but none of the canonical values authored -> benign default.
        if (canonicalCount == 0)
        {
            return MachinePolicyDetection.NotConfigured();
        }

        // Step A: oversized (REG_SZ length bounded BEFORE any content parse).
        if (IsOversized(hasSchema, schema) ||
            IsOversized(hasDesktop, desktop) ||
            IsOversized(hasChef, chef) ||
            IsOversized(hasService, service))
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Oversized);
        }

        // Step B: explicit registry-type checks.
        if (hasSchema && schema!.Kind != MachinePolicyValueKind.Dword)
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.TypeMismatch);
        }
        if ((hasDesktop && desktop!.Kind != MachinePolicyValueKind.String) ||
            (hasChef && chef!.Kind != MachinePolicyValueKind.String) ||
            (hasService && service!.Kind != MachinePolicyValueKind.String))
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.TypeMismatch);
        }

        // Step C: all four canonical values are required once policy is authored.
        if (!hasSchema || !hasDesktop || !hasChef || !hasService)
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Partial);
        }

        // Step D: only schema version 1 is accepted.
        if (schema!.DwordValue != MachinePolicyContract.SupportedSchemaVersion)
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.UnsupportedSchema);
        }

        // Step E: exact closed-enum parse (empty / unknown -> UnknownEnum).
        if (!TryParseDesktopAccess(desktop!.StringValue, out MachinePolicyDesktopAccess desktopAccess) ||
            !TryParseCapability(chef!.StringValue, out MachinePolicyCapability chefKeys) ||
            !TryParseCapability(service!.StringValue, out MachinePolicyCapability windowsService))
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.UnknownEnum);
        }

        // Step F: semantic conflict — self-service can never carry an enabled cap.
        if (desktopAccess == MachinePolicyDesktopAccess.SelfService &&
            (chefKeys == MachinePolicyCapability.Enabled || windowsService == MachinePolicyCapability.Enabled))
        {
            return MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Conflicting);
        }

        // Step G: valid configured policy.
        return desktopAccess == MachinePolicyDesktopAccess.SelfService
            ? MachinePolicyDetection.ConfiguredSelfService()
            : MachinePolicyDetection.ConfiguredOrganizationManaged(chefKeys, windowsService);
    }

    private static bool IsOversized(bool present, MachinePolicyRawValue? raw)
        => present
           && raw!.Kind == MachinePolicyValueKind.String
           && raw.EncodedLength > MachinePolicyContract.MaxRegSzLength;

    private static bool TryParseDesktopAccess(string? value, out MachinePolicyDesktopAccess result)
    {
        switch (value)
        {
            case MachinePolicyContract.DesktopAccessSelfService:
                result = MachinePolicyDesktopAccess.SelfService;
                return true;
            case MachinePolicyContract.DesktopAccessOrganizationManaged:
                result = MachinePolicyDesktopAccess.OrganizationManaged;
                return true;
            default:
                result = MachinePolicyDesktopAccess.Undetermined;
                return false;
        }
    }

    private static bool TryParseCapability(string? value, out MachinePolicyCapability result)
    {
        switch (value)
        {
            case MachinePolicyContract.CapabilityDisabled:
                result = MachinePolicyCapability.Disabled;
                return true;
            case MachinePolicyContract.CapabilityEnabled:
                result = MachinePolicyCapability.Enabled;
                return true;
            default:
                result = MachinePolicyCapability.Disabled;
                return false;
        }
    }
}

// The bounded customer-facing display state.
public enum MachinePolicyDisplayState
{
    PersonalSetup,
    ManagedByOrganization,
    OrganizationPolicyNeedsAttention,
}

// A bounded projection of the detection for customer-visible surfaces. It exposes
// NO registry paths, raw values, schema internals, exception text, enum names,
// capability internals, or identifiers — only a bounded display state and bounded
// copy. It carries NO action that mutates managed policy (a normal user cannot
// edit administrator-controlled policy).
public sealed class MachinePolicyStatusProjection
{
    private MachinePolicyStatusProjection(
        MachinePolicyDisplayState displayState,
        string title,
        string description)
    {
        DisplayState = displayState;
        Title = title;
        Description = description;
    }

    public MachinePolicyDisplayState DisplayState { get; }

    public string Title { get; }

    public string Description { get; }

    public static MachinePolicyStatusProjection Project(MachinePolicyDetection detection)
    {
        if (detection is null)
        {
            return NeedsAttention();
        }

        switch (detection.State)
        {
            case MachinePolicyState.NotConfigured:
                return Personal();

            case MachinePolicyState.Configured:
                return detection.DesktopAccess == MachinePolicyDesktopAccess.OrganizationManaged
                    ? Managed()
                    : Personal();

            case MachinePolicyState.Invalid:
            case MachinePolicyState.Untrusted:
            default:
                return NeedsAttention();
        }
    }

    private static MachinePolicyStatusProjection Personal()
        => new(
            MachinePolicyDisplayState.PersonalSetup,
            "Personal setup",
            "PAX Cookbook is set up for personal use on this device.");

    private static MachinePolicyStatusProjection Managed()
        => new(
            MachinePolicyDisplayState.ManagedByOrganization,
            "Managed by your organization",
            "Your organization manages PAX Cookbook settings on this device.");

    private static MachinePolicyStatusProjection NeedsAttention()
        => new(
            MachinePolicyDisplayState.OrganizationPolicyNeedsAttention,
            "Organization policy needs attention",
            "A device policy for PAX Cookbook could not be verified. Contact your administrator.");
}
