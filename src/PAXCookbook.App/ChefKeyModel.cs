using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography.X509Certificates;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Chef's Keys domain + route handlers (CK-1).
//
// Turns Chef's Keys into a real, Windows-Credential-Manager-backed credential
// store. PAX Cookbook NEVER stores credential material itself: every create /
// read / update / delete is funnelled through WindowsCredentialStore, which
// writes the per-user WCM vault under the binding target convention
// PAXCookbook:ChefKey:<id>.
//
// Four Chef's Key types:
//   WebLogin          metadata: upn (+ optional tenantId), displayName. No secret.
//   DeviceCode        metadata: upn (+ optional tenantId), displayName. No secret.
//   AppReg-Certificate metadata: tenantId, clientId, certThumbprint, displayName.
//                     The certificate lives in a Personal store (Current User or
//                     Local Machine); the Chef's Key holds ONLY the thumbprint
//                     reference and never records which store it came from.
//   AppReg-Secret     metadata: tenantId, clientId, displayName; secret: clientSecret.
//
// Constraint 14 (secrets never leak) is enforced structurally: the list / detail
// response DTO has NO clientSecret field at all -- only a boolean hasSecret. The
// secret field is write-only; a blank secret on update keeps the existing one.
// The /test route performs LOCAL/structural validation only -- no PAX, no
// interactive sign-in, no Microsoft Graph call. A real Graph connectivity test
// is deferred (CK-3 / X5B).
internal static class ChefKeyModel
{
    internal const string AuthWebLogin = "WebLogin";
    internal const string AuthDeviceCode = "DeviceCode";
    internal const string AuthAppRegCertificate = "AppReg-Certificate";
    internal const string AuthAppRegSecret = "AppReg-Secret";

    private const string TargetPrefix = "PAXCookbook:ChefKey:";
    private const int MaxDisplayNameChars = 120;

    private static readonly string[] AllowedAuthTypes =
    {
        AuthWebLogin, AuthDeviceCode, AuthAppRegCertificate, AuthAppRegSecret,
    };

    // The exhaustive set of request body keys. Any other key is rejected so a
    // caller cannot smuggle an unexpected field (e.g. a secret into a no-secret
    // type) past validation.
    private static readonly HashSet<string> AllowedRequestKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "authType", "displayName", "tenantId", "clientId", "certThumbprint", "upn", "clientSecret",
        };

    private static readonly Regex UpnPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    // ---------------------------------------------------------------------
    // GET /api/v1/chef-keys -- list (metadata only; secrets never included)
    // ---------------------------------------------------------------------
    // The personal `chefKeys` array is unchanged. An additive, read-only
    // `organizationKeys` status object reports whether machine policy AUTHORIZES
    // a FUTURE organization-provided inventory. It is authorization/status only:
    // it enumerates no key, exposes no identifier or secret, and never asserts
    // availability (inventoryLoaded is always false).
    public static (int Status, object Body) List(ManagedChefKeysGateProjection organizationKeys)
    {
        IReadOnlyList<WindowsCredentialStore.CredentialRecord> records =
            WindowsCredentialStore.Enumerate(TargetPrefix + "*");

        var items = new List<object>();
        foreach (WindowsCredentialStore.CredentialRecord rec in records)
        {
            if (!rec.TargetName.StartsWith(TargetPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            string id = rec.TargetName.Substring(TargetPrefix.Length);
            items.Add(BuildItem(id, rec.UserName, rec.HasSecret));
        }

        // Stable, display-friendly ordering by displayName then id.
        items.Sort((a, b) => string.Compare(ItemSortKey(a), ItemSortKey(b), StringComparison.OrdinalIgnoreCase));

        return (200, new
        {
            chefKeys = items,
            organizationKeys = new
            {
                state = organizationKeys.WireState,
                reason = organizationKeys.WireReason,
                readOnly = true,
                certificateOnly = true,
                inventoryLoaded = false,
            },
        });
    }

    // ---------------------------------------------------------------------
    // GET /api/v1/chef-keys -- list (organization INVENTORY projection overload)
    // ---------------------------------------------------------------------
    // The organization status flows from the read-only inventory evaluator
    // (Cycle-4 gate + read-only ProgramData source + bounded parser). The wire
    // object surfaces the REAL projection: the bounded state/reason tokens, the
    // constant read-only / certificate-only markers, the real
    // `inventoryLoaded` flag (true ONLY for authorized_provisioned), and a
    // non-negative `entryCount` present ONLY in the provisioned state (omitted
    // entirely otherwise -- never emitted as 0/null). It still emits NO
    // organization identifier, display name, tenant/client reference,
    // certificate reference, path, ACL, reason detail, or raw source. The
    // personal `chefKeys` array is unchanged and independent.
    public static (int Status, object Body) List(OrganizationInventoryProjection organizationInventory)
    {
        IReadOnlyList<WindowsCredentialStore.CredentialRecord> records =
            WindowsCredentialStore.Enumerate(TargetPrefix + "*");

        var items = new List<object>();
        foreach (WindowsCredentialStore.CredentialRecord rec in records)
        {
            if (!rec.TargetName.StartsWith(TargetPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            string id = rec.TargetName.Substring(TargetPrefix.Length);
            items.Add(BuildItem(id, rec.UserName, rec.HasSecret));
        }

        // Stable, display-friendly ordering by displayName then id.
        items.Sort((a, b) => string.Compare(ItemSortKey(a), ItemSortKey(b), StringComparison.OrdinalIgnoreCase));

        // `entryCount` is present ONLY when the inventory is provisioned; in every
        // other bounded state the property is OMITTED entirely (not 0/null).
        object organizationKeys = organizationInventory.InventoryLoaded
            ? new
            {
                state = organizationInventory.WireState,
                reason = organizationInventory.WireReason,
                readOnly = true,
                certificateOnly = true,
                inventoryLoaded = organizationInventory.InventoryLoaded,
                entryCount = organizationInventory.EntryCount,
            }
            : new
            {
                state = organizationInventory.WireState,
                reason = organizationInventory.WireReason,
                readOnly = true,
                certificateOnly = true,
                inventoryLoaded = organizationInventory.InventoryLoaded,
            };

        return (200, new
        {
            chefKeys = items,
            organizationKeys,
        });
    }

    // ---------------------------------------------------------------------
    // GET /api/v1/chef-keys -- list (inventory projection + certificate AGGREGATE)
    // ---------------------------------------------------------------------
    // Identical to the projection-only overload, plus BOUNDED AGGREGATE COUNTS
    // from the read-only certificate catalog. The counts appear ONLY in the
    // provisioned state, alongside `entryCount`, and are tallies and nothing
    // else: no per-entry result, no organization identifier, no display name, no
    // tenant/client reference, no certificate reference, fingerprint, or other
    // certificate field, no validity timestamp, no key or provider name, no store
    // path, and no reason detail. A resolved count is a METADATA MATCH COUNT
    // ONLY, and a usable count is a BOUNDED LOCAL CHECK COUNT ONLY -- neither
    // asserts that a key is trusted, chain-valid, non-revoked, tenant-accepted,
    // Recipe-bound, or Bake-authorized, and every capability on the aggregate
    // stays constant-false. `clientAuthNotAllowedCount` names the TLS Client
    // Authentication PURPOSE POLICY state, never a client identifier. The
    // personal `chefKeys` array is unchanged and independent.
    public static (int Status, object Body) List(
        OrganizationInventoryProjection organizationInventory,
        OrganizationCertificateAggregate? organizationCertificates)
        => List(organizationInventory, organizationCertificates, null);

    // ---------------------------------------------------------------------
    // GET /api/v1/chef-keys -- list (+ bounded ORGANIZATION SELECTOR array)
    // ---------------------------------------------------------------------
    // Identical to the aggregate overload, plus a PRESENTATION-ONLY nested
    // `selectableKeys` array so the Mini-Kitchen Chef's Key dropdown can offer a
    // separate, read-only organization group. Each element carries EXACTLY three
    // fields -- `organizationKeyId`, `displayName`, `eligible` -- and nothing
    // else: no tenant/client reference, no certificate SHA-256, thumbprint,
    // subject, issuer, serial, store or location, no admin state, no raw
    // readiness reason, no private-key detail, no path, secret, token, or claim.
    //
    // `eligible` means LOCALLY READY BY POLICY / INVENTORY / RESOLUTION /
    // USABILITY. It does NOT mean runnable: an organization-bound Recipe is
    // ALWAYS `organization_key_not_yet_runnable`. The array appears ONLY in the
    // provisioned state and is OMITTED ENTIRELY when nothing is selectable. This
    // overload only PROJECTS: there is no new route and no mutation route.
    public static (int Status, object Body) List(
        OrganizationInventoryProjection organizationInventory,
        OrganizationCertificateAggregate? organizationCertificates,
        IReadOnlyList<OrganizationKeySelectorEntry>? organizationSelector)
    {
        List<object> items = BuildPersonalItems();

        // Fail closed: a missing aggregate is treated as "nothing was resolved".
        OrganizationCertificateAggregate certificates =
            organizationCertificates ?? OrganizationCertificateAggregate.None();

        // Insertion-ordered so the projected key sequence is identical to the
        // anonymous shapes this replaced.
        var organizationKeys = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = organizationInventory.WireState,
            ["reason"] = organizationInventory.WireReason,
            ["readOnly"] = true,
            ["certificateOnly"] = true,
            ["inventoryLoaded"] = organizationInventory.InventoryLoaded,
        };

        if (organizationInventory.InventoryLoaded)
        {
            organizationKeys["entryCount"] = organizationInventory.EntryCount;
            organizationKeys["resolvedMetadataCount"] = certificates.ResolvedMetadataCount;
            organizationKeys["notFoundCount"] = certificates.NotFoundCount;
            organizationKeys["ambiguousCount"] = certificates.AmbiguousCount;
            organizationKeys["referenceMissingCount"] = certificates.ReferenceMissingCount;
            organizationKeys["disabledCount"] = certificates.DisabledCount;
            organizationKeys["catalogUnavailable"] = certificates.CatalogUnavailable;
            organizationKeys["usableCount"] = certificates.UsableCount;
            organizationKeys["notYetValidCount"] = certificates.NotYetValidCount;
            organizationKeys["expiredCount"] = certificates.ExpiredCount;
            organizationKeys["clientAuthNotAllowedCount"] = certificates.ClientAuthNotAllowedCount;
            organizationKeys["digitalSignatureNotAllowedCount"] = certificates.DigitalSignatureNotAllowedCount;
            organizationKeys["unsupportedKeyAlgorithmCount"] = certificates.UnsupportedKeyAlgorithmCount;
            organizationKeys["privateKeyUnavailableCount"] = certificates.PrivateKeyUnavailableCount;
            organizationKeys["usabilityInvalidCount"] = certificates.UsabilityInvalidCount;
            organizationKeys["usabilityUnavailable"] = certificates.UsabilityUnavailable;

            // The selector array exists ONLY here, and only when non-empty. Each
            // element owns its own closed three-field wire shape, so this
            // projection cannot widen it.
            if (organizationSelector is not null && organizationSelector.Count > 0)
            {
                var selectable = new List<object>();
                foreach (OrganizationKeySelectorEntry entry in organizationSelector)
                {
                    selectable.Add(entry.ToWireObject());
                }
                organizationKeys["selectableKeys"] = selectable;
            }
        }

        return (200, new
        {
            chefKeys = items,
            organizationKeys,
        });
    }

    // The personal Chef's Keys array, unchanged: Windows-Credential-Manager
    // metadata only, in the same stable display order, with no secret.
    private static List<object> BuildPersonalItems()
    {
        IReadOnlyList<WindowsCredentialStore.CredentialRecord> records =
            WindowsCredentialStore.Enumerate(TargetPrefix + "*");

        var items = new List<object>();
        foreach (WindowsCredentialStore.CredentialRecord rec in records)
        {
            if (!rec.TargetName.StartsWith(TargetPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            string id = rec.TargetName.Substring(TargetPrefix.Length);
            items.Add(BuildItem(id, rec.UserName, rec.HasSecret));
        }

        items.Sort((a, b) => string.Compare(ItemSortKey(a), ItemSortKey(b), StringComparison.OrdinalIgnoreCase));
        return items;
    }

    // ---------------------------------------------------------------------
    // GET /api/v1/chef-keys/{id} -- detail (metadata only)
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Get(string id)
    {
        if (!IsValidChefKeyId(id))
        {
            return (400, new { error = "invalid_chef_key_id", chefKeyId = id });
        }

        WindowsCredentialStore.CredentialRecord? rec = WindowsCredentialStore.Read(TargetPrefix + id);
        if (rec is null)
        {
            return (404, new { error = "not_found", chefKeyId = id });
        }

        return (200, new { chefKey = BuildItem(id, rec.UserName, rec.HasSecret) });
    }

    // ---------------------------------------------------------------------
    // POST /api/v1/chef-keys -- create (server generates id, writes WCM)
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Create(object? body)
    {
        if (body is not Dictionary<string, object?> request)
        {
            return (400, new { error = "invalid_json" });
        }

        (bool ok, object? error, ChefKeyFields fields) = ValidateRequest(request, isUpdate: false, existingAuthType: null);
        if (!ok)
        {
            return (400, error!);
        }

        string id = Guid.NewGuid().ToString();
        string target = TargetPrefix + id;

        string metadataJson = BuildMetadataJson(fields);
        if (metadataJson.Length > WindowsCredentialStore.MaxUserNameChars)
        {
            return (400, new { error = "metadata_too_long", message = "The Chef's Key metadata exceeds the Windows Credential Manager limit. Use a shorter display name." });
        }

        byte[]? secret = fields.AuthType == AuthAppRegSecret && fields.ClientSecret is { Length: > 0 }
            ? Encoding.Unicode.GetBytes(fields.ClientSecret)
            : null;

        try
        {
            WindowsCredentialStore.Write(target, metadataJson, secret);
        }
        finally
        {
            if (secret is not null) { Array.Clear(secret, 0, secret.Length); }
        }

        bool hasSecret = fields.AuthType == AuthAppRegSecret && fields.ClientSecret is { Length: > 0 };
        return (201, new { id, chefKey = BuildItem(id, metadataJson, hasSecret) });
    }

    // ---------------------------------------------------------------------
    // PUT /api/v1/chef-keys/{id} -- update metadata; secret write-only (blank = keep)
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Update(string id, object? body)
    {
        if (!IsValidChefKeyId(id))
        {
            return (400, new { error = "invalid_chef_key_id", chefKeyId = id });
        }
        if (body is not Dictionary<string, object?> request)
        {
            return (400, new { error = "invalid_json" });
        }

        string target = TargetPrefix + id;
        WindowsCredentialStore.CredentialRecord? existing = WindowsCredentialStore.Read(target);
        if (existing is null)
        {
            return (404, new { error = "not_found", chefKeyId = id });
        }

        string existingAuthType = ReadAuthTypeFromMetadata(existing.UserName);

        (bool ok, object? error, ChefKeyFields fields) = ValidateRequest(request, isUpdate: true, existingAuthType: existingAuthType);
        if (!ok)
        {
            return (400, error!);
        }

        string metadataJson = BuildMetadataJson(fields);
        if (metadataJson.Length > WindowsCredentialStore.MaxUserNameChars)
        {
            return (400, new { error = "metadata_too_long", message = "The Chef's Key metadata exceeds the Windows Credential Manager limit. Use a shorter display name." });
        }

        // Secret resolution. For AppReg-Secret: a supplied non-empty clientSecret
        // replaces the stored secret; a blank/absent clientSecret keeps the
        // existing one (read-and-rewrite, never serialized). For every other type
        // the credential carries no secret.
        byte[]? secret = null;
        bool keptExisting = false;
        try
        {
            if (fields.AuthType == AuthAppRegSecret)
            {
                if (fields.ClientSecret is { Length: > 0 })
                {
                    secret = Encoding.Unicode.GetBytes(fields.ClientSecret);
                }
                else
                {
                    secret = WindowsCredentialStore.ReadSecretBytes(target);
                    keptExisting = secret is { Length: > 0 };
                }
            }

            WindowsCredentialStore.Write(target, metadataJson, secret);
        }
        finally
        {
            if (secret is not null) { Array.Clear(secret, 0, secret.Length); }
        }

        bool hasSecret = fields.AuthType == AuthAppRegSecret &&
            ((fields.ClientSecret is { Length: > 0 }) || keptExisting);

        return (200, new { id, chefKey = BuildItem(id, metadataJson, hasSecret) });
    }

    // ---------------------------------------------------------------------
    // DELETE /api/v1/chef-keys/{id} -- delete WCM entry
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Delete(string id)
    {
        if (!IsValidChefKeyId(id))
        {
            return (400, new { error = "invalid_chef_key_id", chefKeyId = id });
        }

        bool removed = WindowsCredentialStore.Delete(TargetPrefix + id);
        if (!removed)
        {
            return (404, new { error = "not_found", chefKeyId = id });
        }

        return (200, new { id, deleted = true });
    }

    // ---------------------------------------------------------------------
    // POST /api/v1/chef-keys/{id}/test -- LOCAL structural validation only
    //
    // Bounded for CK-1: NO PAX, NO interactive sign-in, NO Microsoft Graph call.
    // Validates required fields per type, UPN format for WebLogin/DeviceCode,
    // the personal certificate reference across BOTH fixed Personal stores
    // (read-only), and secret presence for AppReg-Secret. A real Graph
    // connectivity test is deferred to CK-3 / X5B.
    //
    // Cycle 37 -- the certificate check no longer claims a CurrentUser-only
    // result, and no longer reports an unreadable store as a definite absence.
    // Store location is NEVER persisted in the Chef's Key: the reference is
    // re-resolved from scratch every time Test runs.
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Test(string id)
    {
        if (!IsValidChefKeyId(id))
        {
            return (400, new { error = "invalid_chef_key_id", chefKeyId = id });
        }

        WindowsCredentialStore.CredentialRecord? rec = WindowsCredentialStore.Read(TargetPrefix + id);
        if (rec is null)
        {
            return (404, new { error = "not_found", chefKeyId = id });
        }

        object? meta = JsonModel.Parse(rec.UserName);
        var metaDict = meta as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string authType = Str(metaDict, "authType") ?? "unknown";

        // The ONLY certificate-store I/O on the Test path. It runs at most once
        // per request, and ONLY for a certificate key that actually carries a
        // thumbprint -- every other Chef's Key type opens no store at all.
        PersonalCertificateResolution certResolution = PersonalCertificateResolution.Invalid;
        if (string.Equals(authType, AuthAppRegCertificate, StringComparison.Ordinal))
        {
            string? thumb = Str(metaDict, "certThumbprint");
            if (thumb is { Length: > 0 })
            {
                certResolution = ResolvePersonalCertificateReference(thumb);
            }
        }

        return ProjectTestResponse(authType, metaDict, rec.HasSecret, certResolution);
    }

    // Pure projection of the Test response. It performs NO I/O of any kind: the
    // certificate resolution reaches it as an already-closed state produced by
    // the fixed two-store shim. Keeping it separate lets every resolution state
    // be proven against the real response shape without touching a certificate
    // store or the Windows Credential Manager.
    internal static (int Status, object Body) ProjectTestResponse(
        string authType,
        Dictionary<string, object?> metaDict,
        bool hasSecret,
        PersonalCertificateResolution certResolution)
    {
        var checks = new List<object>();
        bool pass = true;

        void Check(string name, bool ok, string detail)
        {
            checks.Add(new { name, ok, detail });
            if (!ok) { pass = false; }
        }

        switch (authType)
        {
            case AuthWebLogin:
            case AuthDeviceCode:
            {
                string? upn = Str(metaDict, "upn");
                bool present = upn is { Length: > 0 };
                Check("upn_present", present, present ? "Sign-in name is set." : "Sign-in name (UPN) is missing.");
                bool formatOk = present && UpnPattern.IsMatch(upn!);
                Check("upn_format", formatOk, formatOk ? "Sign-in name looks like user@domain." : "Sign-in name is not in user@domain form.");
                break;
            }
            case AuthAppRegCertificate:
            {
                string? tenantId = Str(metaDict, "tenantId");
                string? clientId = Str(metaDict, "clientId");
                string? thumb = Str(metaDict, "certThumbprint");
                Check("tenant_id_present", tenantId is { Length: > 0 }, tenantId is { Length: > 0 } ? "Tenant ID is set." : "Tenant ID is missing.");
                Check("client_id_present", clientId is { Length: > 0 }, clientId is { Length: > 0 } ? "Application (client) ID is set." : "Application (client) ID is missing.");
                bool thumbPresent = thumb is { Length: > 0 };
                Check("cert_thumbprint_present", thumbPresent, thumbPresent ? "Certificate thumbprint is set." : "Certificate thumbprint is missing.");
                if (thumbPresent)
                {
                    (bool certOk, string certDetail) = ProjectCertInStoreCheck(certResolution);
                    Check("cert_in_store", certOk, certDetail);
                }
                break;
            }
            case AuthAppRegSecret:
            {
                string? tenantId = Str(metaDict, "tenantId");
                string? clientId = Str(metaDict, "clientId");
                Check("tenant_id_present", tenantId is { Length: > 0 }, tenantId is { Length: > 0 } ? "Tenant ID is set." : "Tenant ID is missing.");
                Check("client_id_present", clientId is { Length: > 0 }, clientId is { Length: > 0 } ? "Application (client) ID is set." : "Application (client) ID is missing.");
                Check("client_secret_present", hasSecret, hasSecret ? "A client secret is stored." : "No client secret is stored.");
                break;
            }
            default:
                Check("auth_type_known", false, $"Unrecognized sign-in type '{authType}'.");
                break;
        }

        string reason = pass
            ? "All local checks passed. A live sign-in is verified when a recipe using this key is baked."
            : "One or more local checks failed. See the details below.";

        return (200, new
        {
            ok = pass,
            status = pass ? "pass" : "fail",
            reason,
            authType,
            graphConnectivityTested = false,
            checks,
        });
    }

    // ---------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------
    private sealed class ChefKeyFields
    {
        public string AuthType = string.Empty;
        public string DisplayName = string.Empty;
        public string? TenantId;
        public string? ClientId;
        public string? CertThumbprint;
        public string? Upn;
        public string? ClientSecret;
    }

    private static (bool Ok, object? Error, ChefKeyFields Fields) ValidateRequest(
        Dictionary<string, object?> request, bool isUpdate, string? existingAuthType)
    {
        var fields = new ChefKeyFields();

        // Reject unknown / extra fields outright.
        foreach (string key in request.Keys)
        {
            if (!AllowedRequestKeys.Contains(key))
            {
                return (false, Fail("unknown_field", key, "This field is not recognized for a Chef's Key."), fields);
            }
        }

        string? authType = Str(request, "authType");
        if (authType is null || Array.IndexOf(AllowedAuthTypes, authType) < 0)
        {
            return (false, Fail("invalid_auth_type", "authType",
                "authType must be one of WebLogin, DeviceCode, AppReg-Certificate, AppReg-Secret."), fields);
        }

        // authType is immutable after create. A type change is a delete + create.
        if (isUpdate && existingAuthType is not null &&
            !string.Equals(authType, existingAuthType, StringComparison.Ordinal))
        {
            return (false, Fail("auth_type_immutable", "authType",
                "The sign-in type cannot be changed on an existing Chef's Key. Delete it and create a new one."), fields);
        }

        fields.AuthType = authType;

        string? displayName = Str(request, "displayName");
        if (displayName is null)
        {
            return (false, Fail("display_name_required", "displayName", "A display name is required."), fields);
        }
        if (displayName.Length > MaxDisplayNameChars)
        {
            return (false, Fail("display_name_too_long", "displayName",
                $"The display name must be {MaxDisplayNameChars} characters or fewer."), fields);
        }
        fields.DisplayName = displayName;

        string? tenantId = Str(request, "tenantId");
        string? clientId = Str(request, "clientId");
        string? certThumbprint = Str(request, "certThumbprint");
        string? upn = Str(request, "upn");
        string? clientSecret = Str(request, "clientSecret");

        switch (authType)
        {
            case AuthWebLogin:
            case AuthDeviceCode:
            {
                if (clientId is not null || certThumbprint is not null || clientSecret is not null)
                {
                    return (false, Fail("field_not_allowed_for_type", "clientId/certThumbprint/clientSecret",
                        $"{authType} does not use an application id, certificate, or secret."), fields);
                }
                if (upn is null)
                {
                    return (false, Fail("upn_required", "upn", "A sign-in name (user@domain) is required."), fields);
                }
                if (!UpnPattern.IsMatch(upn))
                {
                    return (false, Fail("invalid_upn", "upn", "The sign-in name must look like user@domain.com."), fields);
                }
                if (tenantId is not null && !Guid.TryParse(tenantId, out _))
                {
                    return (false, Fail("invalid_tenant_id", "tenantId", "Tenant ID, when supplied, must be a GUID."), fields);
                }
                fields.Upn = upn;
                fields.TenantId = tenantId;
                break;
            }
            case AuthAppRegCertificate:
            {
                if (upn is not null || clientSecret is not null)
                {
                    return (false, Fail("field_not_allowed_for_type", "upn/clientSecret",
                        "AppReg-Certificate does not use a sign-in name or a secret."), fields);
                }
                if (tenantId is null || !Guid.TryParse(tenantId, out _))
                {
                    return (false, Fail("invalid_tenant_id", "tenantId", "A Tenant ID (GUID) is required."), fields);
                }
                if (clientId is null || !Guid.TryParse(clientId, out _))
                {
                    return (false, Fail("invalid_client_id", "clientId", "An Application (client) ID (GUID) is required."), fields);
                }
                string? normalized = NormalizeThumbprint(certThumbprint);
                if (normalized is null)
                {
                    return (false, Fail("invalid_cert_thumbprint", "certThumbprint",
                        "A certificate thumbprint (40 hexadecimal characters) is required."), fields);
                }
                fields.TenantId = tenantId;
                fields.ClientId = clientId;
                fields.CertThumbprint = normalized;
                break;
            }
            case AuthAppRegSecret:
            {
                if (upn is not null || certThumbprint is not null)
                {
                    return (false, Fail("field_not_allowed_for_type", "upn/certThumbprint",
                        "AppReg-Secret does not use a sign-in name or a certificate."), fields);
                }
                if (tenantId is null || !Guid.TryParse(tenantId, out _))
                {
                    return (false, Fail("invalid_tenant_id", "tenantId", "A Tenant ID (GUID) is required."), fields);
                }
                if (clientId is null || !Guid.TryParse(clientId, out _))
                {
                    return (false, Fail("invalid_client_id", "clientId", "An Application (client) ID (GUID) is required."), fields);
                }
                // Secret is required on create; on update a blank secret keeps the existing one.
                if (!isUpdate && clientSecret is null)
                {
                    return (false, Fail("client_secret_required", "clientSecret", "A client secret is required."), fields);
                }
                if (clientSecret is { Length: > 0 } &&
                    Encoding.Unicode.GetByteCount(clientSecret) > WindowsCredentialStore.MaxCredentialBlobBytes)
                {
                    return (false, Fail("client_secret_too_long", "clientSecret",
                        "The client secret is too large for secure storage."), fields);
                }
                fields.TenantId = tenantId;
                fields.ClientId = clientId;
                fields.ClientSecret = clientSecret;
                break;
            }
        }

        return (true, null, fields);
    }

    // Validation errors reference field NAMES only -- never a field value, so a
    // submitted secret can never appear in a response (constraint 14).
    private static object Fail(string error, string field, string message) =>
        new { error = "validation_failed", reason = error, field, message };

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    // Build the response item. NEVER carries clientSecret -- only hasSecret.
    private static object BuildItem(string id, string metadataJson, bool hasSecret)
    {
        object? meta = JsonModel.Parse(metadataJson);
        var d = meta as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return new
        {
            id,
            authType = Str(d, "authType") ?? "unknown",
            displayName = Str(d, "displayName") ?? string.Empty,
            tenantId = Str(d, "tenantId"),
            clientId = Str(d, "clientId"),
            certThumbprint = Str(d, "certThumbprint"),
            upn = Str(d, "upn"),
            hasSecret,
        };
    }

    private static string ItemSortKey(object item)
    {
        // The anonymous item exposes displayName + id; reflect to sort.
        System.Reflection.PropertyInfo? dn = item.GetType().GetProperty("displayName");
        System.Reflection.PropertyInfo? idp = item.GetType().GetProperty("id");
        string display = dn?.GetValue(item) as string ?? string.Empty;
        string id = idp?.GetValue(item) as string ?? string.Empty;
        return display + "\u0000" + id;
    }

    private static string BuildMetadataJson(ChefKeyFields fields)
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["authType"] = fields.AuthType,
            ["displayName"] = fields.DisplayName,
        };
        if (fields.TenantId is { Length: > 0 }) { meta["tenantId"] = fields.TenantId; }
        if (fields.ClientId is { Length: > 0 }) { meta["clientId"] = fields.ClientId; }
        if (fields.CertThumbprint is { Length: > 0 }) { meta["certThumbprint"] = fields.CertThumbprint; }
        if (fields.Upn is { Length: > 0 }) { meta["upn"] = fields.Upn; }

        byte[] bytes = JsonModel.SerializeToUtf8Bytes(meta);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string ReadAuthTypeFromMetadata(string metadataJson)
    {
        object? meta = JsonModel.Parse(metadataJson);
        if (meta is Dictionary<string, object?> d)
        {
            return Str(d, "authType") ?? string.Empty;
        }
        return string.Empty;
    }

    // ---------------------------------------------------------------------
    // Cycle 37 -- personal certificate reference resolution across BOTH fixed
    // Personal stores.
    //
    // A Chef's Key holds ONLY a thumbprint. A certificate carrying that
    // thumbprint may live in Current User > Personal or in Local Machine >
    // Personal, so the Test check must inspect BOTH before it may claim a
    // uniqueness result, and must fail closed when it cannot inspect both.
    //
    // Nothing about a certificate is retained: no certificate, key, handle, DER
    // bytes, subject, issuer, serial, provider or container name. No private-key
    // API is used at all. Matching occurrences are counted, never de-duplicated
    // and never preferred; a store is never judged by how many UNRELATED
    // certificates it holds.
    // ---------------------------------------------------------------------

    // The closed set of resolution outcomes. There is no permissive default:
    // anything unknown or malformed fails closed.
    internal enum PersonalCertificateResolution
    {
        Invalid,
        NotFound,
        UniqueCurrentUser,
        UniqueLocalMachine,
        Ambiguous,
        Unavailable,
    }

    // One store's MATCHING-OCCURRENCE count, saturated to the closed 0 / 1 / many
    // domain. Any total above one is already ambiguous, so no larger cap exists.
    internal enum PersonalCertificateStoreMatchCount
    {
        Zero,
        One,
        Many,
    }

    // PURE resolver -- ZERO I/O. Inputs are reference validity plus each fixed
    // store's availability and saturated match count; the output is the closed
    // resolution state. Every decision-table case is provable here without a
    // certificate store.
    internal static PersonalCertificateResolution ResolvePersonalCertificateState(
        bool referenceValid,
        bool currentUserAvailable,
        PersonalCertificateStoreMatchCount currentUserMatches,
        bool localMachineAvailable,
        PersonalCertificateStoreMatchCount localMachineMatches)
    {
        // An invalid reference is decided WITHOUT consulting either store, so the
        // store inputs are deliberately ignored on this path.
        if (!referenceValid)
        {
            return PersonalCertificateResolution.Invalid;
        }

        // Strict WHOLE-CHECK availability. The check claims a CROSS-STORE
        // uniqueness result, which cannot be established from a single store --
        // so one unreadable store fails the whole check even when the other
        // store reports exactly one match.
        if (!currentUserAvailable || !localMachineAvailable)
        {
            return PersonalCertificateResolution.Unavailable;
        }

        // An unmapped enum value fails closed instead of defaulting to success.
        if (!IsKnownMatchCount(currentUserMatches) || !IsKnownMatchCount(localMachineMatches))
        {
            return PersonalCertificateResolution.Unavailable;
        }

        if (currentUserMatches == PersonalCertificateStoreMatchCount.Many ||
            localMachineMatches == PersonalCertificateStoreMatchCount.Many)
        {
            return PersonalCertificateResolution.Ambiguous;
        }

        bool oneCurrentUser = currentUserMatches == PersonalCertificateStoreMatchCount.One;
        bool oneLocalMachine = localMachineMatches == PersonalCertificateStoreMatchCount.One;

        if (oneCurrentUser && oneLocalMachine)
        {
            return PersonalCertificateResolution.Ambiguous;
        }
        if (oneCurrentUser)
        {
            return PersonalCertificateResolution.UniqueCurrentUser;
        }
        if (oneLocalMachine)
        {
            return PersonalCertificateResolution.UniqueLocalMachine;
        }
        return PersonalCertificateResolution.NotFound;
    }

    private static bool IsKnownMatchCount(PersonalCertificateStoreMatchCount value) =>
        value == PersonalCertificateStoreMatchCount.Zero ||
        value == PersonalCertificateStoreMatchCount.One ||
        value == PersonalCertificateStoreMatchCount.Many;

    // Saturate a raw matching-occurrence count. `many` is never truncated to `one`.
    internal static PersonalCertificateStoreMatchCount SaturateMatchCount(int matchCount) => matchCount switch
    {
        <= 0 => PersonalCertificateStoreMatchCount.Zero,
        1 => PersonalCertificateStoreMatchCount.One,
        _ => PersonalCertificateStoreMatchCount.Many,
    };

    // Pure copy mapping for the `cert_in_store` check. ONLY a unique match in
    // exactly one of the two stores passes. No exception text and no engine
    // internals ever reach the customer.
    internal static (bool Ok, string Detail) ProjectCertInStoreCheck(PersonalCertificateResolution resolution) =>
        resolution switch
        {
            PersonalCertificateResolution.UniqueCurrentUser => (true,
                "A certificate with this thumbprint was found in Current User > Personal."),
            PersonalCertificateResolution.UniqueLocalMachine => (true,
                "A certificate with this thumbprint was found in Local Machine > Personal. " +
                "This confirms the certificate reference only; background service access has not been configured or verified."),
            PersonalCertificateResolution.NotFound => (false,
                "No certificate with this thumbprint was found in Current User > Personal or Local Machine > Personal."),
            PersonalCertificateResolution.Ambiguous => (false,
                "More than one certificate with this thumbprint was found. Remove the duplicate certificate reference before using this Chef's Key."),
            PersonalCertificateResolution.Invalid => (false,
                "The certificate thumbprint is not a valid certificate reference."),
            PersonalCertificateResolution.Unavailable => (false,
                "PAX Cookbook could not safely check both Personal certificate stores."),
            _ => (false,
                "PAX Cookbook could not safely check both Personal certificate stores."),
        };

    // FIXED production I/O shim. Exactly ONE Current User > Personal query and
    // exactly ONE Local Machine > Personal query, both hard-coded. There is no
    // caller-supplied location, store, delegate, enumerator or override, so no
    // caller can redirect this resolution or make it run a different number of
    // times.
    private static PersonalCertificateResolution ResolvePersonalCertificateReference(string? thumbprint)
    {
        string? normalized = NormalizeThumbprint(thumbprint);
        if (normalized is null)
        {
            // Fails closed on the reference itself -- NEITHER store is opened.
            return ResolvePersonalCertificateState(
                referenceValid: false,
                currentUserAvailable: false,
                currentUserMatches: PersonalCertificateStoreMatchCount.Zero,
                localMachineAvailable: false,
                localMachineMatches: PersonalCertificateStoreMatchCount.Zero);
        }

        (bool currentUserAvailable, PersonalCertificateStoreMatchCount currentUserMatches) =
            CountCurrentUserPersonalMatches(normalized);
        (bool localMachineAvailable, PersonalCertificateStoreMatchCount localMachineMatches) =
            CountLocalMachinePersonalMatches(normalized);

        return ResolvePersonalCertificateState(
            referenceValid: true,
            currentUserAvailable: currentUserAvailable,
            currentUserMatches: currentUserMatches,
            localMachineAvailable: localMachineAvailable,
            localMachineMatches: localMachineMatches);
    }

    // The ONE fixed Current User > Personal read-only query. Deliberately not
    // shared with the machine-store query so neither can be re-pointed.
    private static (bool Available, PersonalCertificateStoreMatchCount Matches) CountCurrentUserPersonalMatches(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);
            X509Certificate2Collection all = store.Certificates;
            X509Certificate2Collection matches =
                all.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            PersonalCertificateStoreMatchCount saturated = SaturateMatchCount(matches.Count);
            foreach (X509Certificate2 match in matches) { match.Dispose(); }
            foreach (X509Certificate2 certificate in all) { certificate.Dispose(); }
            return (true, saturated);
        }
        catch
        {
            // The store could not be opened or queried. That is NOT an absence,
            // so it is reported as unavailable rather than as zero matches.
            return (false, PersonalCertificateStoreMatchCount.Zero);
        }
    }

    // The ONE fixed Local Machine > Personal read-only query. A match here proves
    // the certificate REFERENCE only -- never private-key access, service
    // ownership, or ACL readiness, none of which are inspected.
    private static (bool Available, PersonalCertificateStoreMatchCount Matches) CountLocalMachinePersonalMatches(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);
            X509Certificate2Collection all = store.Certificates;
            X509Certificate2Collection matches =
                all.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            PersonalCertificateStoreMatchCount saturated = SaturateMatchCount(matches.Count);
            foreach (X509Certificate2 match in matches) { match.Dispose(); }
            foreach (X509Certificate2 certificate in all) { certificate.Dispose(); }
            return (true, saturated);
        }
        catch
        {
            // The store could not be opened or queried. That is NOT an absence,
            // so it is reported as unavailable rather than as zero matches.
            return (false, PersonalCertificateStoreMatchCount.Zero);
        }
    }

    // Chef's Key ids are server-generated GUIDs; the test smoke uses a
    // CK1SMOKE-<guid> namespace. Both satisfy this pattern. Validating the id
    // keeps the constructed WCM target inside the PAXCookbook:ChefKey: namespace
    // and rejects separators / wildcards that could address another credential.
    internal static bool IsValidChefKeyId(string? id) =>
        id is not null && Regex.IsMatch(id, "^[A-Za-z0-9-]{1,64}$");

    // ---------------------------------------------------------------------
    // CK-2: recipe binding resolution (metadata only -- constraint 14)
    //
    // Resolves a recipe's auth.chefKeyId to the non-secret Chef's Key metadata
    // the recipe preview / readiness projection consumes. Reads ONLY the WCM
    // metadata blob (authType, tenantId, clientId, certThumbprint) and the
    // hasSecret boolean; the secret material is NEVER read here. The builder
    // and readiness card consume metadata only -- existence, type, and the
    // non-secret app-registration fields the PAX argv projection emits.
    // ---------------------------------------------------------------------

    // Resolved, secret-free Chef's Key projection for a recipe binding.
    internal sealed record ChefKeyResolved(
        string ChefKeyId,
        string AuthType,
        string RecipeAuthMode,
        string? TenantId,
        string? ClientId,
        string? CertThumbprint,
        bool HasSecret);

    // Recipe auth.mode -> Chef's Key authType (one direction of the binding map).
    // ManagedIdentity (and any other value) binds to no Chef's Key.
    internal static string? CkAuthTypeForRecipeMode(string? recipeMode) => recipeMode switch
    {
        "WebLogin" => AuthWebLogin,
        "DeviceCode" => AuthDeviceCode,
        "AppRegistrationSecret" => AuthAppRegSecret,
        "AppRegistrationCertificate" => AuthAppRegCertificate,
        _ => null,
    };

    // Chef's Key authType -> recipe auth.mode (the reverse direction).
    internal static string? RecipeModeForCkAuthType(string? authType) => authType switch
    {
        AuthWebLogin => "WebLogin",
        AuthDeviceCode => "DeviceCode",
        AuthAppRegSecret => "AppRegistrationSecret",
        AuthAppRegCertificate => "AppRegistrationCertificate",
        _ => null,
    };

    // Resolve a chefKeyId to its non-secret metadata, or null when the id is
    // malformed or no such Chef's Key exists in the per-user WCM vault.
    public static ChefKeyResolved? ResolveForRecipe(string? chefKeyId)
    {
        if (!IsValidChefKeyId(chefKeyId))
        {
            return null;
        }
        WindowsCredentialStore.CredentialRecord? rec = WindowsCredentialStore.Read(TargetPrefix + chefKeyId);
        if (rec is null)
        {
            return null;
        }
        object? meta = JsonModel.Parse(rec.UserName);
        var d = meta as Dictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string authType = Str(d, "authType") ?? string.Empty;
        return new ChefKeyResolved(
            ChefKeyId: chefKeyId!,
            AuthType: authType,
            RecipeAuthMode: RecipeModeForCkAuthType(authType) ?? string.Empty,
            TenantId: Str(d, "tenantId"),
            ClientId: Str(d, "clientId"),
            CertThumbprint: Str(d, "certThumbprint"),
            HasSecret: rec.HasSecret);
    }

    // Detect a recipe whose bound Chef's Key type does not match its sign-in
    // mode. Returns true (with the recipe mode and the bound key's type) only
    // when auth.chefKeyId names an EXISTING key whose RecipeAuthMode differs
    // from auth.mode. Returns false when no key is bound, the key is absent or
    // malformed (existence is enforced by the readiness / cook gates), or the
    // types match. Metadata only -- the secret is never read (constraint 14).
    // Used by the recipe create / update routes to reject a mismatched recipe
    // before it is persisted (belt-and-suspenders behind the builder guard).
    public static bool TryGetRecipeModeMismatch(
        Dictionary<string, object?> recipe, out string recipeMode, out string chefKeyType)
    {
        recipeMode = string.Empty;
        chefKeyType = string.Empty;
        string? chefKeyId = null;
        if (recipe.TryGetValue("auth", out object? authObj) &&
            authObj is Dictionary<string, object?> auth)
        {
            recipeMode = Str(auth, "mode") ?? string.Empty;
            chefKeyId = Str(auth, "chefKeyId");
        }
        if (string.IsNullOrWhiteSpace(chefKeyId))
        {
            return false;
        }
        ChefKeyResolved? resolved = ResolveForRecipe(chefKeyId);
        if (resolved is null)
        {
            return false;
        }
        if (string.Equals(resolved.RecipeAuthMode, recipeMode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        chefKeyType = resolved.AuthType;
        return true;
    }

    // ---------------------------------------------------------------------
    // CK-3: bake-time secret read for credential injection (constraint 14)
    //
    // The ONLY secret-bearing read on the recipe-binding path. Returns the raw
    // WCM blob (the UTF-16LE clientSecret bytes) for the bound AppRegistrationSecret
    // Chef's Key, or null when the id is malformed / absent / has no secret. The
    // caller (CookCredentialInjection) converts it to GRAPH_CLIENT_SECRET on the
    // CHILD environment and MUST zero the returned bytes. The result is NEVER
    // serialized into a route response, DTO, log, sentinel, or report. This wrapper
    // exists so the WCM target convention (PAXCookbook:ChefKey:<id>) stays
    // encapsulated in this model rather than leaking to the supervisor.
    // ---------------------------------------------------------------------
    internal static byte[]? ReadRecipeSecretBytes(string? chefKeyId)
    {
        if (!IsValidChefKeyId(chefKeyId))
        {
            return null;
        }
        return WindowsCredentialStore.ReadSecretBytes(TargetPrefix + chefKeyId!);
    }

    private static string? NormalizeThumbprint(string? raw)
    {
        if (raw is null) { return null; }
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (Uri.IsHexDigit(c)) { sb.Append(char.ToUpperInvariant(c)); }
        }
        string hex = sb.ToString();
        return hex.Length == 40 ? hex : null;
    }

    private static string? Str(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out object? v) && v is string s && s.Trim().Length > 0 ? s.Trim() : null;
}
