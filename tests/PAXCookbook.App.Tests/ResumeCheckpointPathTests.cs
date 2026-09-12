using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 31 - server-side validation and normalization of the operator-supplied
// resume checkpoint path.
//
// The resume route previously accepted any non-empty string and then decided the
// PAX resume shape from the ".json" suffix alone. These tests pin the replacement
// contract: fully-qualified only, canonicalized, classified by what is actually
// on disk, and reduced to a case-insensitive identity key that two resumes of the
// same location must share.
//
// Nothing here spawns a process, runs PAX, or starts a Bake. Real directories and
// files are created under the OS temp directory because classification is by
// EXISTENCE and a fake would not exercise it.
public class ResumeCheckpointPathTests : IDisposable
{
    private readonly List<string> _roots = new();

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // A leftover temp directory is harmless; never fail a test on cleanup.
            }
        }

        GC.SuppressFinalize(this);
    }

    private string NewRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "paxcb-c31-cp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private string NewDirectory(string name)
    {
        string dir = Path.Combine(NewRoot(), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string NewFile(string name, string content = "{}")
    {
        string file = Path.Combine(NewRoot(), name);
        File.WriteAllText(file, content);
        return file;
    }

    // ---- refusals -----------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n ")]
    public void Empty_or_whitespace_only_is_refused(string? candidate)
    {
        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(candidate);

        Assert.False(result.Ok);
        Assert.Equal(ResumeCheckpointRejection.Empty, result.Rejection);
        Assert.Equal(string.Empty, result.CanonicalPath);
        Assert.Equal(string.Empty, result.IdentityKey);
    }

    [Theory]
    [InlineData("foo\\bar")]
    [InlineData("checkpoint.json")]
    [InlineData(".\\out")]
    [InlineData("..\\out")]
    [InlineData("\\foo")]          // rooted but not fully qualified
    [InlineData("C:foo")]          // drive-relative: resolves against a per-drive cwd
    public void A_path_that_is_not_fully_qualified_is_refused(string candidate)
    {
        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(candidate);

        Assert.False(result.Ok);
        Assert.Equal(ResumeCheckpointRejection.NotFullyQualified, result.Rejection);
    }

    [Fact]
    public void A_malformed_path_is_refused_rather_than_throwing()
    {
        string root = NewRoot();
        string malformed = root + "\0name";

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(malformed);

        Assert.False(result.Ok);
        Assert.Equal(ResumeCheckpointRejection.Malformed, result.Rejection);

        // Positive control: the SAME string without the embedded null is not
        // malformed, so this assertion is discriminating a real property of the
        // input rather than rejecting everything.
        ResumeCheckpointPathResult control = ResumeCheckpointPath.Validate(root + "name");
        Assert.False(control.Ok);
        Assert.Equal(ResumeCheckpointRejection.NotFound, control.Rejection);
        Assert.NotEqual(result.Rejection, control.Rejection);
    }

    [Fact]
    public void A_nonexistent_absolute_path_is_refused()
    {
        string missing = Path.Combine(NewRoot(), "no-such-checkpoint");

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(missing);

        Assert.False(result.Ok);
        Assert.Equal(ResumeCheckpointRejection.NotFound, result.Rejection);
    }

    [Theory]
    [InlineData("checkpoint.txt")]
    [InlineData("checkpoint")]
    [InlineData("checkpoint.jsonl")]
    [InlineData("checkpoint.json.bak")]
    public void An_existing_file_that_is_not_a_json_checkpoint_is_refused(string name)
    {
        string file = NewFile(name);

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(file);

        Assert.False(result.Ok);
        Assert.Equal(ResumeCheckpointRejection.WrongKind, result.Rejection);

        // Positive control: renaming the very same existing file to .json makes it
        // acceptable, so the refusal above is about the suffix of a real file and
        // not about files in general.
        string accepted = Path.ChangeExtension(file, ".json");
        File.Move(file, accepted);
        Assert.True(ResumeCheckpointPath.Validate(accepted).Ok);
    }

    // ---- acceptance and classification --------------------------------------

    [Fact]
    public void An_existing_directory_is_classified_as_a_checkpoint_folder()
    {
        string dir = NewDirectory("run-output");

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(dir);

        Assert.True(result.Ok);
        Assert.Equal(ResumeCheckpointKind.Directory, result.Kind);
        Assert.Equal(dir, result.CanonicalPath);
        Assert.Equal(ResumeCheckpointRejection.None, result.Rejection);
    }

    [Fact]
    public void An_existing_json_file_is_classified_as_an_explicit_resume_file()
    {
        string file = NewFile("checkpoint.json");

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(file);

        Assert.True(result.Ok);
        Assert.Equal(ResumeCheckpointKind.JsonFile, result.Kind);
        Assert.Equal(file, result.CanonicalPath);
    }

    [Fact]
    public void An_existing_json_file_is_matched_case_insensitively()
    {
        string file = NewFile("Checkpoint.JSON");

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(file);

        Assert.True(result.Ok);
        Assert.Equal(ResumeCheckpointKind.JsonFile, result.Kind);
    }

    [Fact]
    public void A_directory_named_like_a_json_file_is_still_a_folder()
    {
        // The bug this cycle fixes: suffix-based classification would render this
        // DIRECTORY as an explicit `-Resume "<file>"` target.
        string dir = NewDirectory("checkpoint.json");

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(dir);

        Assert.True(result.Ok);
        Assert.Equal(ResumeCheckpointKind.Directory, result.Kind);

        // Positive control: a real FILE with the identical name classifies the
        // other way, so Kind is genuinely driven by what is on disk.
        string file = NewFile("checkpoint.json");
        Assert.Equal(ResumeCheckpointKind.JsonFile, ResumeCheckpointPath.Validate(file).Kind);
    }

    [Fact]
    public void A_relative_segment_inside_an_absolute_path_is_canonicalized_away()
    {
        string dir = NewDirectory("run-output");
        string parent = Path.GetDirectoryName(dir)!;
        string noisy = Path.Combine(parent, "run-output", "..", "run-output");

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(noisy);

        Assert.True(result.Ok);
        Assert.Equal(dir, result.CanonicalPath);
        Assert.DoesNotContain("..", result.CanonicalPath, StringComparison.Ordinal);
    }

    // ---- identity normalization ---------------------------------------------

    [Fact]
    public void A_trailing_separator_does_not_change_the_identity()
    {
        string dir = NewDirectory("run-output");

        ResumeCheckpointPathResult bare = ResumeCheckpointPath.Validate(dir);
        ResumeCheckpointPathResult trailing = ResumeCheckpointPath.Validate(dir + "\\");
        ResumeCheckpointPathResult many = ResumeCheckpointPath.Validate(dir + "\\\\");
        ResumeCheckpointPathResult alt = ResumeCheckpointPath.Validate(dir + "/");

        Assert.True(bare.Ok && trailing.Ok && many.Ok && alt.Ok);
        Assert.Equal(bare.IdentityKey, trailing.IdentityKey);
        Assert.Equal(bare.IdentityKey, many.IdentityKey);
        Assert.Equal(bare.IdentityKey, alt.IdentityKey);
        Assert.Equal(bare.CanonicalPath, trailing.CanonicalPath);
    }

    [Fact]
    public void Identity_is_case_insensitive_but_two_different_folders_never_collide()
    {
        string dir = NewDirectory("Run-Output");

        ResumeCheckpointPathResult mixed = ResumeCheckpointPath.Validate(dir);
        ResumeCheckpointPathResult lower = ResumeCheckpointPath.Validate(dir.ToLowerInvariant());
        ResumeCheckpointPathResult upper = ResumeCheckpointPath.Validate(dir.ToUpperInvariant());

        Assert.True(mixed.Ok && lower.Ok && upper.Ok);
        Assert.Equal(mixed.IdentityKey, lower.IdentityKey);
        Assert.Equal(mixed.IdentityKey, upper.IdentityKey);

        // Positive control: a genuinely different folder must NOT share the key,
        // so the equality above is not the trivial consequence of a constant key.
        string other = NewDirectory("Run-Output-2");
        Assert.NotEqual(mixed.IdentityKey, ResumeCheckpointPath.Validate(other).IdentityKey);
    }

    [Fact]
    public void A_folder_and_a_json_file_never_share_an_identity()
    {
        string dir = NewDirectory("run-output");
        string file = Path.Combine(dir, "checkpoint.json");
        File.WriteAllText(file, "{}");

        ResumeCheckpointPathResult folder = ResumeCheckpointPath.Validate(dir);
        ResumeCheckpointPathResult json = ResumeCheckpointPath.Validate(file);

        Assert.True(folder.Ok && json.Ok);
        Assert.NotEqual(folder.IdentityKey, json.IdentityKey);
    }

    // ---- root-safe trailing-separator trimming -------------------------------
    //
    // Exercised through the pure helper because a UNC root cannot be created on a
    // build agent, and because the drive root must be provably preserved without
    // depending on any particular drive layout.

    [Theory]
    [InlineData("C:\\", "C:\\")]
    [InlineData("Z:\\", "Z:\\")]
    // .NET reports the UNC root as "\\server\share" WITHOUT a trailing separator
    // (unlike a drive root, which includes it), so the trimmer stops exactly at
    // the share boundary. The trailing separator is dropped; the server and share
    // names are never truncated, which is the property that matters.
    [InlineData("\\\\server\\share\\", "\\\\server\\share")]
    [InlineData("\\\\server\\share", "\\\\server\\share")]
    public void A_path_root_is_never_truncated(string root, string expected)
    {
        string trimmed = ResumeCheckpointPath.TrimTrailingSeparatorsToRoot(root);

        Assert.Equal(expected, trimmed);
        // A drive root trimmed to "C:" would become DRIVE-RELATIVE, and a UNC root
        // trimmed past the share would name a different (or no) location. Both are
        // silent corruptions this guard exists to prevent.
        Assert.True(Path.IsPathFullyQualified(trimmed));
        Assert.Equal(Path.GetPathRoot(root), Path.GetPathRoot(trimmed));
        Assert.True(trimmed.Length >= (Path.GetPathRoot(root) ?? string.Empty).Length);
    }

    [Theory]
    [InlineData("C:\\PAX\\out\\", "C:\\PAX\\out")]
    [InlineData("C:\\PAX\\out\\\\", "C:\\PAX\\out")]
    [InlineData("C:\\PAX\\out/", "C:\\PAX\\out")]
    [InlineData("\\\\server\\share\\out\\", "\\\\server\\share\\out")]
    [InlineData("\\\\server\\share\\a\\b\\\\", "\\\\server\\share\\a\\b")]
    public void A_non_root_path_does_have_its_trailing_separators_trimmed(string input, string expected)
    {
        // Positive control for the theory above: the trimmer is not a no-op, so
        // "the root was not trimmed" is a real distinction.
        Assert.Equal(expected, ResumeCheckpointPath.TrimTrailingSeparatorsToRoot(input));
    }

    [Fact]
    public void The_drive_root_survives_full_validation_end_to_end()
    {
        string systemRoot = Path.GetPathRoot(Path.GetTempPath())!;

        ResumeCheckpointPathResult result = ResumeCheckpointPath.Validate(systemRoot);

        Assert.True(result.Ok);
        Assert.Equal(ResumeCheckpointKind.Directory, result.Kind);
        Assert.Equal(systemRoot, result.CanonicalPath);
        Assert.True(Path.IsPathFullyQualified(result.CanonicalPath));
        Assert.Equal(systemRoot.ToUpperInvariant(), result.IdentityKey);
    }
}
