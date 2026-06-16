using System.Collections.Generic;

namespace PAXCookbookSetup.Payload;

// Phase 11 — payload source resolution.
//
// Setup install/update/repair needs a payload root folder containing
// manifest.json and the staged files referenced by the manifest. Prior
// phases required `--payload-root <dir>` on the command line. Phase 11
// adds an embedded payload zip resource so a single PAXCookbookSetup.exe
// can install with no external folder.
//
// Resolution order (Program.cs):
//   1. If --payload-root <dir> is supplied -> DirectoryPayloadSourceResolver.
//   2. Else if the running assembly contains a "PAXCookbook.Payload.zip"
//      embedded resource -> EmbeddedPayloadSourceResolver extracts to
//      %TEMP%\PAXCookbookPayload_<UTC>_<random>\ with path-traversal
//      guards, then returns that as the payload root.
//   3. Else fail clearly with a payload-not-found error.

public interface IPayloadSourceResolver
{
    // Returns a fully-resolved PayloadSource on success, or a result
    // with Success=false + Error populated. Implementations must not
    // throw; they capture errors in the result.
    PayloadSource Resolve();
}

public sealed record PayloadSource(
    bool Success,
    string? PayloadRoot,
    string Origin,                  // "directory" | "embedded" | "none"
    string? TempExtractionRoot,     // non-null when Origin=="embedded"
    string? Error,
    IReadOnlyList<string>? Warnings = null);
