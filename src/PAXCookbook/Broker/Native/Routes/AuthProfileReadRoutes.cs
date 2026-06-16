using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3c -- read-only AuthProfiles surface (metadata only).
//
//   GET /api/v1/auth/profiles        -- list of all auth profiles.
//   GET /api/v1/auth/profiles/{id}   -- single profile by id.
//
// IMPORTANT: NO SECRET MATERIAL IS RETURNED.
//   - The cred_man_target column holds a Windows Credential Manager
//     LOOKUP KEY (e.g. "PAXCookbook|<profileId>|clientSecret"). The
//     PowerShell broker exposes this column verbatim because it is
//     not the secret itself -- the secret lives encrypted in
//     Credential Manager and is fetched only at the moment a cook
//     authenticates. The native broker matches that contract.
//   - The cert_thumbprint / cert_store columns identify a certificate
//     in the user/machine cert store; they are public identifiers,
//     not key material.
//   - Re-auth (verify) is NOT exposed in Stage 3c -- the PS broker's
//     "POST /api/v1/auth/profiles/{id}/verify" gate stays out of the
//     native broker until a later stage.
public static class AuthProfileReadRoutes
{
    public static void Register(IEndpointRouteBuilder app, SqliteWorkspaceReader reader)
    {
        app.MapGet("/api/v1/auth/profiles", () =>
        {
            var rows = reader.TryListAuthProfiles();
            if (rows is null)
            {
                return Results.Json(new
                {
                    error = "workspace_database_unavailable",
                },
                statusCode: StatusCodes.Status500InternalServerError);
            }
            return Results.Json(new
            {
                authProfiles = rows.Select(r => new
                {
                    authProfileId      = r.AuthProfileId,
                    name               = r.Name,
                    mode               = r.Mode,
                    tenantId           = r.TenantId,
                    clientId           = r.ClientId,
                    credManTarget      = r.CredManTarget,
                    certThumbprint     = r.CertThumbprint,
                    certStore          = r.CertStore,
                    description        = r.Description,
                    lastVerifiedAt     = r.LastVerifiedAt,
                    lastVerifiedResult = r.LastVerifiedResult,
                    createdAt          = r.CreatedAt,
                    updatedAt          = r.UpdatedAt,
                }).ToArray(),
            });
        });

        app.MapGet("/api/v1/auth/profiles/{id}", (string id) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.Json(new { error = "auth_profile_id_required" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var row = reader.GetAuthProfileById(id);
            if (row is null)
            {
                return Results.Json(new { error = "auth_profile_not_found", authProfileId = id },
                    statusCode: StatusCodes.Status404NotFound);
            }
            return Results.Json(new
            {
                authProfile = new
                {
                    authProfileId      = row.AuthProfileId,
                    name               = row.Name,
                    mode               = row.Mode,
                    tenantId           = row.TenantId,
                    clientId           = row.ClientId,
                    credManTarget      = row.CredManTarget,
                    certThumbprint     = row.CertThumbprint,
                    certStore          = row.CertStore,
                    description        = row.Description,
                    lastVerifiedAt     = row.LastVerifiedAt,
                    lastVerifiedResult = row.LastVerifiedResult,
                    createdAt          = row.CreatedAt,
                    updatedAt          = row.UpdatedAt,
                },
            });
        });
    }
}
