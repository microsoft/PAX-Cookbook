using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- auth_profiles table mutation surface (INSERT /
// UPDATE / DELETE). Ports Add-AuthProfileRow / Update-AuthProfileRow
// / Remove-AuthProfileRow from Routes/AuthProfiles.ps1.
//
// Connections are per-call, ReadWrite mode. Matches the lifecycle
// RecipeMutationStore uses for the recipes table.
//
// The native broker NEVER writes the auth_profiles row from inside
// the SQLite read path -- SqliteWorkspaceReader opens Mode=ReadOnly
// and would fail any UPDATE. This store therefore owns the writeable
// connection for the auth-profile family.
//
// Side-effects scope: cred_man_target is written when the secret
// bind succeeds (and cleared on delete). last_verified_at /
// last_verified_result are stamped by the test service on a best-
// effort basis (failures are swallowed in-route -- the test result
// is the source of truth for the client).
public sealed class AuthProfileMutationStore
{
    private readonly string _connectionString;

    public AuthProfileMutationStore(string databaseFilePath)
    {
        if (string.IsNullOrWhiteSpace(databaseFilePath))
            throw new ArgumentException("databaseFilePath is required", nameof(databaseFilePath));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode       = SqliteOpenMode.ReadWrite,
        }.ToString();
    }

    public AuthProfileRow? GetById(string authProfileId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT auth_profile_id, name, mode, tenant_id, client_id,
       cred_man_target, cert_thumbprint, cert_store, description,
       last_verified_at, last_verified_result,
       created_at, updated_at
FROM auth_profiles
WHERE auth_profile_id = $id;";
        cmd.Parameters.AddWithValue("$id", authProfileId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AuthProfileRow(
            AuthProfileId:      r.GetString(0),
            Name:               r.GetString(1),
            Mode:               r.GetString(2),
            TenantId:           r.GetString(3),
            ClientId:           r.GetString(4),
            CredManTarget:      r.IsDBNull(5)  ? null : r.GetString(5),
            CertThumbprint:     r.IsDBNull(6)  ? null : r.GetString(6),
            CertStore:          r.IsDBNull(7)  ? null : r.GetString(7),
            Description:        r.IsDBNull(8)  ? null : r.GetString(8),
            LastVerifiedAt:     r.IsDBNull(9)  ? null : r.GetString(9),
            LastVerifiedResult: r.IsDBNull(10) ? null : r.GetString(10),
            CreatedAt:          r.GetString(11),
            UpdatedAt:          r.GetString(12));
    }

    public bool NameInUse(string name, string? excludeProfileId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = excludeProfileId is null
            ? "SELECT 1 FROM auth_profiles WHERE name = $name LIMIT 1;"
            : "SELECT 1 FROM auth_profiles WHERE name = $name AND auth_profile_id != $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        if (excludeProfileId is not null)
            cmd.Parameters.AddWithValue("$id", excludeProfileId);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    public void Insert(AuthProfileRow row)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO auth_profiles
    (auth_profile_id, name, mode, tenant_id, client_id,
     cred_man_target, cert_thumbprint, cert_store, description,
     last_verified_at, last_verified_result,
     created_at, updated_at)
VALUES
    ($id, $name, $mode, $tenant_id, $client_id,
     $cred_man_target, $cert_thumbprint, $cert_store, $description,
     $last_verified_at, $last_verified_result,
     $created_at, $updated_at);";
        cmd.Parameters.AddWithValue("$id",                   row.AuthProfileId);
        cmd.Parameters.AddWithValue("$name",                 row.Name);
        cmd.Parameters.AddWithValue("$mode",                 row.Mode);
        cmd.Parameters.AddWithValue("$tenant_id",            row.TenantId);
        cmd.Parameters.AddWithValue("$client_id",            row.ClientId);
        cmd.Parameters.AddWithValue("$cred_man_target",      (object?)row.CredManTarget       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cert_thumbprint",      (object?)row.CertThumbprint      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cert_store",           (object?)row.CertStore           ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$description",          (object?)row.Description         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_verified_at",     (object?)row.LastVerifiedAt      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last_verified_result", (object?)row.LastVerifiedResult  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created_at",           row.CreatedAt);
        cmd.Parameters.AddWithValue("$updated_at",           row.UpdatedAt);
        cmd.ExecuteNonQuery();
    }

    // Full-field update (except mode + auth_profile_id, which are
    // both immutable). cred_man_target is updated through SetCredManTarget;
    // last_verified_* are updated through SetLastVerified.
    public int UpdateMutableFields(
        string authProfileId,
        string name,
        string tenantId,
        string clientId,
        string? description,
        string? certThumbprint,
        string? certStore,
        string updatedAt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE auth_profiles
SET name            = $name,
    tenant_id       = $tenant_id,
    client_id       = $client_id,
    description     = $description,
    cert_thumbprint = $cert_thumbprint,
    cert_store      = $cert_store,
    updated_at      = $updated_at
WHERE auth_profile_id = $id;";
        cmd.Parameters.AddWithValue("$name",            name);
        cmd.Parameters.AddWithValue("$tenant_id",       tenantId);
        cmd.Parameters.AddWithValue("$client_id",       clientId);
        cmd.Parameters.AddWithValue("$description",     (object?)description    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cert_thumbprint", (object?)certThumbprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cert_store",      (object?)certStore      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at",      updatedAt);
        cmd.Parameters.AddWithValue("$id",              authProfileId);
        return cmd.ExecuteNonQuery();
    }

    public int SetCredManTarget(string authProfileId, string? credManTarget, string updatedAt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE auth_profiles
SET cred_man_target = $target,
    updated_at      = $updated_at
WHERE auth_profile_id = $id;";
        cmd.Parameters.AddWithValue("$target",     (object?)credManTarget ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$id",         authProfileId);
        return cmd.ExecuteNonQuery();
    }

    public int SetLastVerified(string authProfileId, string lastVerifiedAt, string lastVerifiedResult)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE auth_profiles
SET last_verified_at     = $at,
    last_verified_result = $result
WHERE auth_profile_id = $id;";
        cmd.Parameters.AddWithValue("$at",     lastVerifiedAt);
        cmd.Parameters.AddWithValue("$result", lastVerifiedResult);
        cmd.Parameters.AddWithValue("$id",     authProfileId);
        return cmd.ExecuteNonQuery();
    }

    public int Delete(string authProfileId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM auth_profiles WHERE auth_profile_id = $id;";
        cmd.Parameters.AddWithValue("$id", authProfileId);
        return cmd.ExecuteNonQuery();
    }
}
