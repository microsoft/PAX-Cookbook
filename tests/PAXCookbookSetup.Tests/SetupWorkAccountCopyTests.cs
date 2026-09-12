using System;
using System.IO;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Setup wizard work-account customer-copy doctrine (Cycle 2).
//
// The Setup wizard's work-account description is customer-facing office-worker
// copy. Under the bounded token / photo doctrine it must NOT tell the customer
// the product "never queries Graph with the sign-in token" / is "token-free"
// (those blanket claims are now false — the bounded profile-photo path exists).
// It must instead use the bounded, truthful office-worker shape. This is a
// source-scan assertion (the WinForms form is not instantiated headlessly).
public sealed class SetupWorkAccountCopyTests
{
    private static string RepoRoot()
    {
        var d = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && d is not null; i++)
        {
            if (File.Exists(Path.Combine(d, "PAXCookbook.sln")))
            {
                return d;
            }

            d = Path.GetDirectoryName(d);
        }

        throw new InvalidOperationException(
            "could not find repo root (PAXCookbook.sln) from " + AppContext.BaseDirectory);
    }

    private static string SetupWizardFormText()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Gui", "SetupWizardForm.cs");
        Assert.True(File.Exists(path), $"expected Setup wizard source missing: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void WorkAccountCopy_UsesBoundedOfficeWorkerShape()
    {
        string text = SetupWizardFormText();
        Assert.Contains(
            "uses your work-account permission only to sign you in and show your",
            text);
        Assert.Contains(
            "does not use this sign-in to read audit or directory data",
            text);
    }

    [Fact]
    public void WorkAccountCopy_DropsSupersededTokenFreeGraphClaims()
    {
        string text = SetupWizardFormText();
        string[] forbidden =
        {
            "never queries Graph with the sign-in token",
            "never uses the sign-in token",
            "token-free",
        };

        foreach (string claim in forbidden)
        {
            Assert.False(
                text.Contains(claim, StringComparison.OrdinalIgnoreCase),
                $"Setup wizard work-account copy still asserts the superseded claim '{claim}'.");
        }
    }
}
