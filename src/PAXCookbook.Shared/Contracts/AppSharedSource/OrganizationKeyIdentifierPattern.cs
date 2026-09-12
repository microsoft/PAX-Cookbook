namespace PAXCookbook.App;

// Single authority for the organization-key identifier shape. Both the Recipe
// validator and OrganizationKeyRecipeBinding consume this one constant so the
// literal cannot drift between the App and Setup compilations.
internal static class OrganizationKeyIdentifierPattern
{
    internal const string Value = "^[A-Za-z0-9._-]{1,128}$";
}
