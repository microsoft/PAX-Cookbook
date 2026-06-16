using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B2 -- service bundle for the recipes/preview + templates/
// materialize surface.
//
// Mirrors the Stage3iB1ServiceBundle pattern. Carries:
//
//   * Clock              -- overridable wall clock for createdAt /
//                           updatedAt stamping in the materialize path
//                           (preview is stateless and doesn't stamp).
//   * RecipeIdFactory    -- overridable ULID generator. Defaults to
//                           Stage3iB1ServiceBundle.NewRecipeId so the
//                           two stages share the same Crockford-base32
//                           generator without duplicating it.
//   * PaxAdapterVersion  -- stamped on draft preview recipes and on
//                           materialized recipes (both file + row).
//   * BundledPaxVersion  -- numeric semver for Test-TemplatePaxCompatibility.
//                           Distinct field from PaxAdapterVersion so a
//                           future divergence between "the script
//                           bundled with the broker" and "the script
//                           string written into recipes.pax_adapter_version"
//                           can be honored without re-plumbing.
//   * CreatedByTemplate  -- provenance block (cookbookVersion,
//                           bundledPaxVersion, releaseChannel).
//   * PreviewPlanProvider-- optional adapter sidecar override. When
//                           null in production, Stage3iB2Wiring builds
//                           the DefaultRecipePreviewPlanProvider over
//                           Stage 3e's PaxInvocationPlanProvider. When
//                           non-null in tests, the wiring uses the
//                           stub verbatim so no real pwsh child runs.
public sealed record Stage3iB2ServiceBundle
{
    public Func<DateTimeOffset>?         Clock                { get; init; }
    public Func<string>?                 RecipeIdFactory      { get; init; }
    public string?                       PaxAdapterVersion    { get; init; }
    public string?                       BundledPaxVersion    { get; init; }
    public RecipeCreatedBy?              CreatedByTemplate    { get; init; }
    public IRecipePreviewPlanProvider?   PreviewPlanProvider  { get; init; }

    public static Stage3iB2ServiceBundle FromVersionInfo(Models.VersionInfo? versionInfo)
    {
        if (versionInfo is null
            || !versionInfo.IsAvailable
            || versionInfo.BundledPax is null
            || string.IsNullOrWhiteSpace(versionInfo.CookbookVersion)
            || string.IsNullOrWhiteSpace(versionInfo.ReleaseChannel))
        {
            return new Stage3iB2ServiceBundle();
        }
        return new Stage3iB2ServiceBundle
        {
            PaxAdapterVersion = versionInfo.BundledPax.Version,
            BundledPaxVersion = versionInfo.BundledPax.Version,
            CreatedByTemplate = new RecipeCreatedBy(
                CookbookVersion:   versionInfo.CookbookVersion!,
                BundledPaxVersion: versionInfo.BundledPax.Version,
                ReleaseChannel:    versionInfo.ReleaseChannel!),
        };
    }
}
