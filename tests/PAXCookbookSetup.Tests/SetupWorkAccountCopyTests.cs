using System;
using System.IO;
using System.Text.RegularExpressions;
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
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public SetupWorkAccountCopyTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

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
        var bodyLiteral = new Regex(
            @"^[ \t]*_signInWorkPanel\.Controls\.Add\(Body\(\s*""(?<copy>[^""\\\r\n]*)""\s*,",
            RegexOptions.Multiline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        string source = SetupWizardFormText();
        MatchCollection matches = bodyLiteral.Matches(source);
        bool missingRefused = bodyLiteral.Matches(string.Empty).Count != 1;
        bool duplicateRefused = bodyLiteral.Matches(source + Environment.NewLine + source).Count != 1;
        _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            universe = "Plain literal first argument of _signInWorkPanel.Controls.Add(Body(...)) in SetupWizardForm.cs",
            predicate = bodyLiteral.ToString(),
            matchCount = matches.Count,
            positiveControl = matches.Count == 1,
            missingRefused,
            duplicateRefused,
        }));
        Assert.True(missingRefused);
        Assert.True(duplicateRefused);
        Assert.Single(matches);

        const string acceptedCopy = "Sign in with your organization's Microsoft work account using the setup file your IT team gives you.";
        const string oldTechnicalCopy =
            "Native Microsoft work-account sign-in using a customer-owned registration that shows the native " +
            "Windows account picker. It requests delegated Microsoft Graph User.Read only; a tenant administrator " +
            "approves it and no secret or certificate is created. PAX Cookbook " +
            "uses your work-account permission only to sign you in and show your profile picture; it " +
            "does not use this sign-in to read audit or directory data. Because the registration shows the " +
            "standard picker, other accounts (including personal or external accounts) may appear, but only " +
            "your configured organization can unlock PAX Cookbook.";
        string copy = matches[0].Groups["copy"].Value;
        var actual = EvaluateOfficeWorkerCopy(copy);
        var positive = EvaluateOfficeWorkerCopy(acceptedCopy);
        var negative = EvaluateOfficeWorkerCopy(oldTechnicalCopy);
        _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            universe = "Extracted Body argument and the accepted/prior technical copy controls; words are whitespace-delimited",
            actual = new { text = copy, actual.WordCount, actual.HasJargon, actual.Accepted },
            positiveControl = new { text = acceptedCopy, positive.WordCount, positive.HasJargon, positive.Accepted },
            negativeControl = new { text = oldTechnicalCopy, negative.WordCount, negative.HasJargon, negative.Accepted },
        }));
        Assert.True(positive.Accepted);
        Assert.False(negative.Accepted);
        Assert.True(negative.HasJargon);
        Assert.True(negative.WordCount > 25);
        Assert.Equal(acceptedCopy, copy);
        Assert.InRange(actual.WordCount, 15, 25);
        Assert.False(actual.HasJargon);
        Assert.True(actual.Accepted);
    }

    private static (int WordCount, bool HasJargon, bool Accepted) EvaluateOfficeWorkerCopy(string copy)
    {
        int wordCount = copy.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        bool hasJargon = Regex.IsMatch(
            copy,
            @"\b(?:registration|delegated|Microsoft\s+Graph|User\.Read|tenant|signInAudience|scope|broker|picker|provision|consent|Azure\s+CLI)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return (wordCount, hasJargon, wordCount is >= 15 and <= 25 && !hasJargon);
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
