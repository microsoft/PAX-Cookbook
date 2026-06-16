using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- structural validator for auth-profile mutation
// bodies. Mirrors Test-AuthProfileBody in Routes/AuthProfiles.ps1.
//
// The PS broker accepts a small AJV-style schema: required-field
// presence + per-field type + per-field regex + a mode-conditional
// rule that toggles certThumbprint/certStore. The native broker
// reproduces the same error envelope (instancePath / keyword /
// message / params) so the SPA's form-bound error display does not
// require a parallel code path.
public static class AuthProfileValidator
{
    // Lowercase GUID without braces; matches the PS broker's
    // Test-AuthProfileGuid regex.
    private static readonly Regex LowercaseGuid = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.Compiled);

    // Uppercase SHA-1 thumbprint exactly 40 hex digits (X.509 store
    // format). The PS broker uppercases on read; the native broker
    // rejects lower-case to make the parity check trivial.
    private static readonly Regex CertThumbprintShape = new(
        @"^[0-9A-F]{40}$",
        RegexOptions.Compiled);

    // X.509 store path -- "LocalMachine\<storeName>" or
    // "CurrentUser\<storeName>" with storeName 1..64 chars.
    private static readonly Regex CertStoreShape = new(
        @"^(?:LocalMachine|CurrentUser)\\[A-Za-z0-9_\-]{1,64}$",
        RegexOptions.Compiled);

    public const int NameMaxLength        = 120;
    public const int DescriptionMaxLength = 2000;

    public sealed record Verdict(
        bool                                          Ok,
        IReadOnlyList<AuthProfileValidationError>     Errors,
        AuthProfileCreateRequest?                     Create,
        AuthProfileUpdateRequest?                     Update);

    public static Verdict ValidateCreate(JsonNode? body)
    {
        var errors = new List<AuthProfileValidationError>();
        if (body is not JsonObject obj)
        {
            errors.Add(new AuthProfileValidationError("", "type",
                "request body must be a JSON object", new { }));
            return new Verdict(false, errors, null, null);
        }

        var mode             = GetString(obj, "mode");
        var name             = GetString(obj, "name");
        var tenantId         = GetString(obj, "tenantId");
        var clientId         = GetString(obj, "clientId");
        var description      = GetString(obj, "description");
        var certThumbprint   = GetString(obj, "certThumbprint");
        var certStore        = GetString(obj, "certStore");

        if (string.IsNullOrEmpty(mode))
        {
            errors.Add(new AuthProfileValidationError("/mode", "required",
                "mode is required", new { }));
        }
        else if (Array.IndexOf(AuthProfileModes.SupportedForMutation, mode) < 0)
        {
            errors.Add(new AuthProfileValidationError("/mode", "enum",
                "mode must be one of: " + string.Join(", ", AuthProfileModes.SupportedForMutation),
                new { allowed = AuthProfileModes.SupportedForMutation }));
        }

        ValidateName(name, errors);
        ValidateGuid(tenantId, "/tenantId", errors);
        ValidateGuid(clientId, "/clientId", errors);
        ValidateDescription(description, errors);

        // Mode-conditional certificate fields.
        if (mode == AuthProfileModes.AppRegistrationCertificate)
        {
            if (string.IsNullOrEmpty(certThumbprint))
            {
                errors.Add(new AuthProfileValidationError("/certThumbprint", "required",
                    "certThumbprint is required when mode = AppRegistrationCertificate",
                    new { }));
            }
            else if (!CertThumbprintShape.IsMatch(certThumbprint))
            {
                errors.Add(new AuthProfileValidationError("/certThumbprint", "format",
                    "certThumbprint must be 40 uppercase hex characters",
                    new { }));
            }

            // certStore is optional; default applied at row construction time.
            if (!string.IsNullOrEmpty(certStore) && !CertStoreShape.IsMatch(certStore))
            {
                errors.Add(new AuthProfileValidationError("/certStore", "format",
                    "certStore must be of the form 'LocalMachine\\<storeName>' or 'CurrentUser\\<storeName>'",
                    new { }));
            }
        }
        else if (mode == AuthProfileModes.AppRegistrationSecret)
        {
            // Reject cert fields supplied for a secret-mode profile
            // (PS broker stops the create with auth_profile_invalid).
            if (!string.IsNullOrEmpty(certThumbprint))
            {
                errors.Add(new AuthProfileValidationError("/certThumbprint", "forbidden",
                    "certThumbprint must not be supplied when mode = AppRegistrationSecret",
                    new { }));
            }
            if (!string.IsNullOrEmpty(certStore))
            {
                errors.Add(new AuthProfileValidationError("/certStore", "forbidden",
                    "certStore must not be supplied when mode = AppRegistrationSecret",
                    new { }));
            }
        }

        if (errors.Count > 0) return new Verdict(false, errors, null, null);

        var req = new AuthProfileCreateRequest(
            Mode:           mode!,
            Name:           name!,
            TenantId:       tenantId!,
            ClientId:       clientId!,
            Description:    string.IsNullOrEmpty(description) ? null : description,
            CertThumbprint: string.IsNullOrEmpty(certThumbprint) ? null : certThumbprint,
            CertStore:      string.IsNullOrEmpty(certStore) ? null : certStore);
        return new Verdict(true, errors, req, null);
    }

    // For PUT: only validates fields that are present. Mode is
    // accepted only when it matches the existing row -- the
    // service layer compares against the row, the validator only
    // captures it for the comparison.
    public static Verdict ValidateUpdate(JsonNode? body, AuthProfileRow existingRow)
    {
        var errors = new List<AuthProfileValidationError>();
        if (body is not JsonObject obj)
        {
            errors.Add(new AuthProfileValidationError("", "type",
                "request body must be a JSON object", new { }));
            return new Verdict(false, errors, null, null);
        }

        var modeIn         = GetString(obj, "mode");
        var name           = GetString(obj, "name");
        var tenantId       = GetString(obj, "tenantId");
        var clientId       = GetString(obj, "clientId");
        var description    = obj.ContainsKey("description")    ? GetString(obj, "description")    : existingRow.Description;
        var certThumbprint = obj.ContainsKey("certThumbprint") ? GetString(obj, "certThumbprint") : existingRow.CertThumbprint;
        var certStore      = obj.ContainsKey("certStore")      ? GetString(obj, "certStore")      : existingRow.CertStore;

        if (!string.IsNullOrEmpty(modeIn) &&
            !string.Equals(modeIn, existingRow.Mode, StringComparison.Ordinal))
        {
            errors.Add(new AuthProfileValidationError("/mode", "immutable",
                "mode is immutable after creation",
                new { currentMode = existingRow.Mode }));
        }

        // For PUT, name / tenantId / clientId default to the existing row
        // when the field is absent from the body (partial-update semantics
        // parity with the PS broker's Update-AuthProfileRow).
        if (string.IsNullOrEmpty(name))     name     = existingRow.Name;
        if (string.IsNullOrEmpty(tenantId)) tenantId = existingRow.TenantId;
        if (string.IsNullOrEmpty(clientId)) clientId = existingRow.ClientId;

        ValidateName(name, errors);
        ValidateGuid(tenantId, "/tenantId", errors);
        ValidateGuid(clientId, "/clientId", errors);
        ValidateDescription(description, errors);

        if (existingRow.Mode == AuthProfileModes.AppRegistrationCertificate)
        {
            if (string.IsNullOrEmpty(certThumbprint))
            {
                errors.Add(new AuthProfileValidationError("/certThumbprint", "required",
                    "certThumbprint is required for AppRegistrationCertificate profiles",
                    new { }));
            }
            else if (!CertThumbprintShape.IsMatch(certThumbprint))
            {
                errors.Add(new AuthProfileValidationError("/certThumbprint", "format",
                    "certThumbprint must be 40 uppercase hex characters",
                    new { }));
            }
            if (!string.IsNullOrEmpty(certStore) && !CertStoreShape.IsMatch(certStore))
            {
                errors.Add(new AuthProfileValidationError("/certStore", "format",
                    "certStore must be of the form 'LocalMachine\\<storeName>' or 'CurrentUser\\<storeName>'",
                    new { }));
            }
        }
        else if (existingRow.Mode == AuthProfileModes.AppRegistrationSecret)
        {
            if (!string.IsNullOrEmpty(certThumbprint))
            {
                errors.Add(new AuthProfileValidationError("/certThumbprint", "forbidden",
                    "certThumbprint must not be set for AppRegistrationSecret profiles",
                    new { }));
            }
            if (!string.IsNullOrEmpty(certStore))
            {
                errors.Add(new AuthProfileValidationError("/certStore", "forbidden",
                    "certStore must not be set for AppRegistrationSecret profiles",
                    new { }));
            }
        }

        if (errors.Count > 0) return new Verdict(false, errors, null, null);

        var req = new AuthProfileUpdateRequest(
            Mode:           string.IsNullOrEmpty(modeIn) ? null : modeIn,
            Name:           name,
            TenantId:       tenantId,
            ClientId:       clientId,
            Description:    string.IsNullOrEmpty(description)    ? null : description,
            CertThumbprint: string.IsNullOrEmpty(certThumbprint) ? null : certThumbprint,
            CertStore:      string.IsNullOrEmpty(certStore)      ? null : certStore);
        return new Verdict(true, errors, null, req);
    }

    private static void ValidateName(string? name, List<AuthProfileValidationError> errors)
    {
        if (string.IsNullOrEmpty(name))
        {
            errors.Add(new AuthProfileValidationError("/name", "required",
                "name is required", new { }));
        }
        else if (name.Length > NameMaxLength)
        {
            errors.Add(new AuthProfileValidationError("/name", "maxLength",
                "name must be <= " + NameMaxLength + " characters",
                new { maxLength = NameMaxLength }));
        }
    }

    private static void ValidateGuid(string? value, string instancePath, List<AuthProfileValidationError> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add(new AuthProfileValidationError(instancePath, "required",
                instancePath + " is required", new { }));
        }
        else if (!LowercaseGuid.IsMatch(value))
        {
            errors.Add(new AuthProfileValidationError(instancePath, "format",
                instancePath + " must be a lowercase GUID without braces",
                new { }));
        }
    }

    private static void ValidateDescription(string? description, List<AuthProfileValidationError> errors)
    {
        if (description is null) return;
        if (description.Length > DescriptionMaxLength)
        {
            errors.Add(new AuthProfileValidationError("/description", "maxLength",
                "description must be <= " + DescriptionMaxLength + " characters",
                new { maxLength = DescriptionMaxLength }));
        }
    }

    private static string? GetString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var n) || n is null) return null;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => n.GetValue<string>(),
            _                                     => null,
        };
    }
}
