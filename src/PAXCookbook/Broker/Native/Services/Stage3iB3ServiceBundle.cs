using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B3 -- service bundle for the recipe-takeout (export /
// validate / import) surface. Mirrors Stage3iB1ServiceBundle +
// Stage3iB2ServiceBundle.
//
// Carries:
//   * Clock                -- overridable wall clock for exportedAtUtc
//                             stamping (export) and createdAt /
//                             updatedAt restamps (import).
//   * RecipeIdFactory      -- overridable ULID generator. Defaults to
//                             Stage3iB1ServiceBundle.NewRecipeId so
//                             all three Stage 3i sub-stages share the
//                             same Crockford-base32 generator.
//   * PaxAdapterVersion    -- stamped on materialized import recipes.
//   * BundledPaxVersion    -- envelope provenance (createdBy block).
//   * CookbookVersion      -- envelope provenance (createdBy block).
//   * ReleaseChannel       -- envelope provenance (createdBy block).
//   * WorkspaceInstallPath -- sanitized workspace fingerprint hint.
//   * ChefKeyLabelLookup   -- optional adapter from authProfileId to
//                             a display label. Production wiring may
//                             pass null when AuthProfilesStore is not
//                             plumbed yet; tests inject a stub.
public sealed record Stage3iB3ServiceBundle
{
    public Func<DateTimeOffset>?    Clock                 { get; init; }
    public Func<string>?            RecipeIdFactory       { get; init; }
    public string?                  PaxAdapterVersion     { get; init; }
    public string?                  BundledPaxVersion     { get; init; }
    public string?                  CookbookVersion       { get; init; }
    public string?                  ReleaseChannel        { get; init; }
    public string?                  WorkspaceInstallPath  { get; init; }
    public Func<string, string?>?   ChefKeyLabelLookup    { get; init; }

    public static Stage3iB3ServiceBundle FromVersionInfo(
        Models.VersionInfo? versionInfo,
        string?             workspaceInstallPath)
    {
        if (versionInfo is null
            || !versionInfo.IsAvailable
            || versionInfo.BundledPax is null
            || string.IsNullOrWhiteSpace(versionInfo.CookbookVersion)
            || string.IsNullOrWhiteSpace(versionInfo.ReleaseChannel))
        {
            return new Stage3iB3ServiceBundle();
        }
        return new Stage3iB3ServiceBundle
        {
            PaxAdapterVersion    = versionInfo.BundledPax.Version,
            BundledPaxVersion    = versionInfo.BundledPax.Version,
            CookbookVersion      = versionInfo.CookbookVersion,
            ReleaseChannel       = versionInfo.ReleaseChannel,
            WorkspaceInstallPath = workspaceInstallPath,
        };
    }
}
