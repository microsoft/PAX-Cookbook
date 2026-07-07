using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Feature D — "What's New" release history. These exercise the real
// AnnouncementModel archive sync (SyncEntriesAt), history build (BuildHistoryAt),
// per-entry show-again (SetShowAgainAt), and image sandboxing (ReadEntryImagesAt)
// against temp fixtures. Scenarios (task step 11):
//   * never syncs / never auto-shows on a fresh Setup or manual-kit install
//   * an update archives a NEW entry, which defaults to shown
//   * per-entry dismiss persists independently of other entries
//   * history is ordered newest-to-oldest
//   * images resolve ONLY from an entry's own folder (non-image / subfolder /
//     outside files never enter the map; remote + traversal refs can't match)
//   * the on-demand browser still lists entries after all are dismissed
//   * a corrected re-ship (same id) keeps the entry's place and the user's choice
//   * retention caps the archive
public sealed class AnnouncementModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _payloadDir;   // <appRoot>/announcements
    private readonly string _archiveRoot;  // %APPDATA% archive

    public AnnouncementModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pax-annc-tests", Guid.NewGuid().ToString("N"));
        _payloadDir = Path.Combine(_root, "App", "announcements");
        _archiveRoot = Path.Combine(_root, "archive", "announcements");
        Directory.CreateDirectory(_payloadDir);
        Directory.CreateDirectory(_archiveRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } } catch { }
    }

    // --- fixture helpers -------------------------------------------------
    private void ShipEntry(string id, string title, string date, string body,
                           (string Name, byte[] Bytes)[]? images = null, string? bodyFile = null)
    {
        string dir = Path.Combine(_payloadDir, id);
        Directory.CreateDirectory(dir);
        string bf = bodyFile ?? "body.md";
        var manifest = new { id, title, date, bodyFile = bf };
        File.WriteAllText(Path.Combine(dir, "entry.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(dir, bf), body, new UTF8Encoding(false));
        if (images is not null)
        {
            foreach ((string name, byte[] bytes) in images)
            {
                File.WriteAllBytes(Path.Combine(dir, name), bytes);
            }
        }
    }

    private void ClearPayload()
    {
        if (Directory.Exists(_payloadDir)) { Directory.Delete(_payloadDir, recursive: true); }
        Directory.CreateDirectory(_payloadDir);
    }

    private void Sync(string? kind, string? status)
        => AnnouncementModel.SyncEntriesAt(_payloadDir, _archiveRoot, kind, status);

    private JsonDocument History(string? kind, string? status)
    {
        object payload = AnnouncementModel.BuildHistoryAt(_archiveRoot, kind, status);
        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }

    private static string[] EntryIds(JsonDocument doc)
        => doc.RootElement.GetProperty("entries").EnumerateArray()
              .Select(e => e.GetProperty("id").GetString()!).ToArray();

    // --- fresh install: never syncs, never auto-shows --------------------
    [Fact]
    public void FreshSetupInstall_NeverSyncs_NeverAutoShows()
    {
        ShipEntry("v1-a", "Sample A", "2026-07-01", "# A");
        Sync("install", "ok");                         // fresh install -> no-op
        using JsonDocument h = History("install", "ok");
        Assert.Empty(EntryIds(h));
        Assert.False(h.RootElement.GetProperty("autoShow").GetBoolean());
    }

    [Fact]
    public void FreshManualKitInstall_NoInstallState_NeverSyncs()
    {
        ShipEntry("v1-a", "Sample A", "2026-07-01", "# A");
        Sync(null, null);                              // no install-state -> no-op
        using JsonDocument h = History(null, null);
        Assert.Empty(EntryIds(h));
        Assert.False(h.RootElement.GetProperty("autoShow").GetBoolean());
    }

    // --- update archives a new entry, defaulting to shown ----------------
    [Fact]
    public void Update_ArchivesNewEntry_DefaultsShown()
    {
        ShipEntry("v1-a", "Sample A", "2026-07-01", "# A\nHello.");
        Sync("update", "ok");
        using JsonDocument h = History("update", "ok");
        Assert.Equal(new[] { "v1-a" }, EntryIds(h));
        Assert.True(h.RootElement.GetProperty("autoShow").GetBoolean());
        Assert.Equal("v1-a", h.RootElement.GetProperty("newestId").GetString());
        JsonElement a = h.RootElement.GetProperty("entries")[0];
        Assert.True(a.GetProperty("showAgain").GetBoolean());
        Assert.Contains("Hello.", a.GetProperty("bodyMarkdown").GetString());
    }

    // --- per-entry dismiss is independent --------------------------------
    [Fact]
    public void PerEntryDismiss_IsIndependent_NewerStillShows()
    {
        ShipEntry("v1-a", "Sample A", "2026-07-01", "# A");
        Sync("update", "ok");
        AnnouncementModel.SetShowAgainAt(_archiveRoot, "v1-a", showAgain: false);

        // A dismissed -> autoShow false (A is newest and hidden).
        using (JsonDocument h1 = History("update", "ok"))
        {
            Assert.False(h1.RootElement.GetProperty("autoShow").GetBoolean());
        }

        // A later, newer entry arrives -> defaults shown -> autoShow true again,
        // and A stays dismissed. Order newest-first: B, A.
        ClearPayload();
        ShipEntry("v2-b", "Sample B", "2026-07-15", "# B");
        Sync("update", "ok");
        using JsonDocument h2 = History("update", "ok");
        Assert.Equal(new[] { "v2-b", "v1-a" }, EntryIds(h2));
        Assert.True(h2.RootElement.GetProperty("autoShow").GetBoolean());
        Assert.Equal("v2-b", h2.RootElement.GetProperty("newestId").GetString());
        JsonElement[] entries = h2.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.True(entries[0].GetProperty("showAgain").GetBoolean());   // B shown
        Assert.False(entries[1].GetProperty("showAgain").GetBoolean());  // A still hidden
    }

    // --- ordering newest-to-oldest ---------------------------------------
    [Fact]
    public void History_OrderedNewestToOldest()
    {
        ShipEntry("mid", "Mid", "2026-07-10", "# mid"); Sync("update", "ok"); ClearPayload();
        ShipEntry("old", "Old", "2026-01-01", "# old"); Sync("update", "ok"); ClearPayload();
        ShipEntry("new", "New", "2026-12-31", "# new"); Sync("update", "ok");
        using JsonDocument h = History("update", "ok");
        Assert.Equal(new[] { "new", "mid", "old" }, EntryIds(h));
    }

    // --- images: only the entry's own top-level image files --------------
    [Fact]
    public void Images_OnlyFromOwnFolder_NonImageAndOutsideExcluded()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        ShipEntry("v1-img", "With image", "2026-07-01",
            "# Title\n\n![hero](hero.png)\n\n![evil](../secret.png)\n\n![remote](https://evil.example/x.png)",
            images: new[] { ("hero.png", png) });
        // Noise that must NOT enter the image map: a non-image file and a subfolder image.
        string entryDir = Path.Combine(_payloadDir, "v1-img");
        File.WriteAllText(Path.Combine(entryDir, "notes.txt"), "not an image");
        Directory.CreateDirectory(Path.Combine(entryDir, "sub"));
        File.WriteAllBytes(Path.Combine(entryDir, "sub", "nested.png"), png);
        // A sensitive file OUTSIDE the entry folder that a traversal ref might target.
        File.WriteAllBytes(Path.Combine(_payloadDir, "secret.png"), png);

        Sync("update", "ok");
        using JsonDocument h = History("update", "ok");
        JsonElement imgs = h.RootElement.GetProperty("entries")[0].GetProperty("images");
        string[] keys = imgs.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "hero.png" }, keys);                 // ONLY the sandboxed image
        Assert.StartsWith("data:image/png;base64,", imgs.GetProperty("hero.png").GetString());
        // The archived folder never received the non-image, the subfolder, or the outside file.
        string archived = Path.Combine(_archiveRoot, "v1-img");
        Assert.True(File.Exists(Path.Combine(archived, "hero.png")));
        Assert.False(File.Exists(Path.Combine(archived, "notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(archived, "sub")));
        Assert.False(File.Exists(Path.Combine(archived, "secret.png")));
    }

    [Fact]
    public void ReadEntryImages_RejectsSvgAndUnknownTypes()
    {
        string dir = Path.Combine(_archiveRoot, "x");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.svg"), "<svg onload=alert(1)></svg>");
        File.WriteAllBytes(Path.Combine(dir, "b.png"), new byte[] { 1, 2, 3 });
        var map = AnnouncementModel.ReadEntryImagesAt(dir);
        Assert.True(map.ContainsKey("b.png"));
        Assert.False(map.ContainsKey("a.svg"));   // SVG excluded (script risk)
    }

    // --- on-demand browser still lists entries after all dismissed -------
    [Fact]
    public void OnDemand_ListsEntries_EvenAfterAllDismissed()
    {
        ShipEntry("v1-a", "Sample A", "2026-07-01", "# A"); Sync("update", "ok"); ClearPayload();
        ShipEntry("v2-b", "Sample B", "2026-07-15", "# B"); Sync("update", "ok");
        AnnouncementModel.SetShowAgainAt(_archiveRoot, "v1-a", false);
        AnnouncementModel.SetShowAgainAt(_archiveRoot, "v2-b", false);

        using JsonDocument h = History("update", "ok");
        // autoShow is off (nothing wants to pop up), but the history is still
        // fully browsable on demand.
        Assert.False(h.RootElement.GetProperty("autoShow").GetBoolean());
        Assert.Equal(new[] { "v2-b", "v1-a" }, EntryIds(h));
    }

    // --- defensive: archive present but a fresh install never auto-shows -
    [Fact]
    public void ArchivePresent_ButInstallKind_NeverAutoShows()
    {
        ShipEntry("v1-a", "Sample A", "2026-07-01", "# A");
        Sync("update", "ok");                     // archive now has an entry
        using JsonDocument h = History("install", "ok");   // ...but kind flips to install
        Assert.False(h.RootElement.GetProperty("autoShow").GetBoolean());
        Assert.Single(EntryIds(h));               // still browsable, just no popup
    }

    // --- corrected re-ship keeps place + the user's choice ---------------
    [Fact]
    public void CorrectedReship_SameId_KeepsPlaceAndChoice_RefreshesBody()
    {
        ShipEntry("v1-a", "Sample A", "2026-07-01", "# A\nOriginal.");
        Sync("update", "ok");
        AnnouncementModel.SetShowAgainAt(_archiveRoot, "v1-a", false);

        // Re-ship the SAME id with corrected body (a typo fix in a later release).
        ClearPayload();
        ShipEntry("v1-a", "Sample A (fixed)", "2026-07-01", "# A\nCorrected copy.");
        Sync("update", "ok");

        using JsonDocument h = History("update", "ok");
        Assert.Equal(new[] { "v1-a" }, EntryIds(h));
        JsonElement a = h.RootElement.GetProperty("entries")[0];
        Assert.False(a.GetProperty("showAgain").GetBoolean());     // choice preserved
        Assert.Equal("Sample A (fixed)", a.GetProperty("title").GetString());
        Assert.Contains("Corrected copy.", a.GetProperty("bodyMarkdown").GetString());
        Assert.False(h.RootElement.GetProperty("autoShow").GetBoolean()); // still hidden
    }

    // --- retention caps the archive --------------------------------------
    [Fact]
    public void Retention_CapsArchive_ToNewest()
    {
        // Ship RetentionMax + 2 entries with ascending dates; oldest two prune.
        int total = AnnouncementModel.RetentionMax + 2;
        for (int i = 0; i < total; i++)
        {
            ClearPayload();
            string date = new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd");
            ShipEntry($"e-{i:D3}", $"Entry {i}", date, $"# Entry {i}");
            Sync("update", "ok");
        }
        using JsonDocument h = History("update", "ok");
        string[] ids = EntryIds(h);
        Assert.Equal(AnnouncementModel.RetentionMax, ids.Length);
        // The two oldest (e-000, e-001) were pruned; newest present.
        Assert.DoesNotContain("e-000", ids);
        Assert.DoesNotContain("e-001", ids);
        Assert.Equal($"e-{(total - 1):D3}", ids[0]);
        Assert.False(Directory.Exists(Path.Combine(_archiveRoot, "e-000")));
    }

    // ===================================================================
    // Adversarial image-handling (Track 1). The image sandbox is enforced in
    // the C# model: the images map delivered to the renderer is built ONLY from
    // real, allowed-extension files physically inside the entry's own folder,
    // resolved through any links to a real path inside that folder. Remote and
    // traversal references never become map keys; the model never fetches; a
    // corrupt file degrades gracefully; SVG is excluded; body Markdown is passed
    // through verbatim for the safe (no-HTML-passthrough) renderer to neutralize.
    // ===================================================================

    private static readonly byte[] TinyPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // 1 — `../` / absolute-path traversal image refs never resolve or leak.
    [Fact]
    public void Adversarial_TraversalImageRef_NeverResolves_OutsideFileNotArchived()
    {
        // A "secret" image OUTSIDE the entry folder that a traversal ref targets.
        File.WriteAllBytes(Path.Combine(_payloadDir, "secret.png"), TinyPng);
        ShipEntry("adv-trav", "Traversal", "2026-07-01",
            "# t\n\n![a](../secret.png)\n\n![b](..\\..\\secret.png)\n\n![c](/Windows/System32/x.png)\n\n![d](C:/secret.png)");
        Sync("update", "ok");
        using JsonDocument h = History("update", "ok");
        JsonElement entry = h.RootElement.GetProperty("entries")[0];
        // No traversal/absolute reference ever becomes a map key (the map has only
        // real files inside the entry folder — here, none).
        Assert.Empty(entry.GetProperty("images").EnumerateObject());
        // The outside file was never copied into the archived entry folder.
        Assert.False(File.Exists(Path.Combine(_archiveRoot, "adv-trav", "secret.png")));
        // The body is returned verbatim; the renderer (not the model) drops the refs.
        Assert.Contains("../secret.png", entry.GetProperty("bodyMarkdown").GetString());
    }

    // 2 — remote URL image refs never become a map key and trigger no fetch.
    [Fact]
    public void Adversarial_RemoteUrlImageRef_NoMapKey_NoFetch()
    {
        ShipEntry("adv-remote", "Remote", "2026-07-01",
            "# r\n\n![a](https://evil.example/track.png)\n\n![b](http://x.example/y.gif)");
        Sync("update", "ok");
        using JsonDocument h = History("update", "ok");
        // Only local files ever enter the map; the model reads local files only —
        // it makes no network call by construction (no HTTP client anywhere here).
        Assert.Empty(h.RootElement.GetProperty("entries")[0].GetProperty("images").EnumerateObject());
    }

    // 3 — SVG is excluded across case + double-extension tricks.
    [Fact]
    public void Adversarial_Svg_Excluded_AllVariants()
    {
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        ShipEntry("adv-svg", "Svg", "2026-07-01", "# s\n\n![](a.svg)");
        string dir = Path.Combine(_payloadDir, "adv-svg");
        File.WriteAllBytes(Path.Combine(dir, "a.svg"), svg);              // .svg -> excluded
        File.WriteAllBytes(Path.Combine(dir, "B.SVG"), svg);              // .SVG (case) -> excluded
        File.WriteAllBytes(Path.Combine(dir, "poster.png.svg"), svg);     // last ext .svg -> excluded
        File.WriteAllBytes(Path.Combine(dir, "poster.svg.png"), TinyPng); // last ext .png -> included as png
        File.WriteAllBytes(Path.Combine(dir, "script.png"), svg);         // .png ext, SVG/script CONTENT
        Sync("update", "ok");
        using JsonDocument h = History("update", "ok");
        JsonElement imgs = h.RootElement.GetProperty("entries")[0].GetProperty("images");
        string[] keys = imgs.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // Only the two .png-terminated files pass the extension gate.
        Assert.Equal(new[] { "poster.svg.png", "script.png" }, keys);
        // No .svg-terminated file was copied into the archive at all.
        Assert.False(File.Exists(Path.Combine(_archiveRoot, "adv-svg", "a.svg")));
        Assert.False(File.Exists(Path.Combine(_archiveRoot, "adv-svg", "B.SVG")));
        Assert.False(File.Exists(Path.Combine(_archiveRoot, "adv-svg", "poster.png.svg")));
        // The SVG-content-in-a-.png file is served as data:image/png — inert as an
        // <img> source (an <img> never executes embedded script regardless of the
        // actual bytes), so extension-based gating is safe here.
        Assert.StartsWith("data:image/png;base64,", imgs.GetProperty("script.png").GetString());
    }

    // 4 — raw HTML / <script> in the body is passed through VERBATIM by the model
    //     (the model is not the HTML gate — the React renderer escapes it) and
    //     never crashes the model.
    [Fact]
    public void Adversarial_HtmlOrScriptInBody_ReturnedVerbatim_NoCrash()
    {
        string body = "# hi\n\n<script>alert(1)</script>\n\n<img src=x onerror=alert(2)>\n\n<b>bold</b>";
        ShipEntry("adv-html", "Html", "2026-07-01", body);
        Sync("update", "ok");
        using JsonDocument h = History("update", "ok");
        string returned = h.RootElement.GetProperty("entries")[0].GetProperty("bodyMarkdown").GetString()!;
        // Verbatim: the model neither executes nor strips it; the safe renderer
        // (React text nodes, no dangerouslySetInnerHTML) neutralizes it downstream.
        Assert.Contains("<script>alert(1)</script>", returned);
        Assert.Contains("onerror=alert(2)", returned);
    }

    // 5 — corrupt / zero-byte / non-image bytes with an image extension degrade
    //     gracefully (no unhandled exception; broken-image data URIs, not a crash).
    [Fact]
    public void Adversarial_CorruptZeroByteNonImage_GracefulNoThrow()
    {
        ShipEntry("adv-corrupt", "Corrupt", "2026-07-01", "# c\n\n![](zero.png) ![](garbage.jpg) ![](truncated.gif)");
        string dir = Path.Combine(_payloadDir, "adv-corrupt");
        File.WriteAllBytes(Path.Combine(dir, "zero.png"), Array.Empty<byte>());          // zero-byte
        File.WriteAllBytes(Path.Combine(dir, "garbage.jpg"), new byte[] { 0, 1, 2, 3 });  // non-image binary
        File.WriteAllText(Path.Combine(dir, "truncated.gif"), "GIF89a<truncated>");        // corrupt header
        Exception? ex = Record.Exception(() =>
        {
            Sync("update", "ok");
            using JsonDocument h = History("update", "ok");
        });
        Assert.Null(ex); // never throws
        Dictionary<string, string> map = AnnouncementModel.ReadEntryImagesAt(Path.Combine(_archiveRoot, "adv-corrupt"));
        Assert.Equal("data:image/png;base64,", map["zero.png"]); // empty but valid, inert data URI
        Assert.True(map.ContainsKey("garbage.jpg"));
        Assert.True(map.ContainsKey("truncated.gif"));
    }

    // 6 — a symlink inside an entry folder pointing OUTSIDE it is excluded (the
    //     sandbox resolves real paths and refuses to follow a link out). Skips the
    //     link assertion gracefully if this environment can't create symlinks.
    [Fact]
    public void Adversarial_SymlinkPointingOutside_Excluded()
    {
        string secret = Path.Combine(_root, "outside-secret.png");
        File.WriteAllBytes(secret, new byte[] { 9, 9, 9, 9 });
        string dir = Path.Combine(_archiveRoot, "adv-link");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "real.png"), TinyPng); // a genuine in-folder image
        string link = Path.Combine(dir, "leak.png");
        bool linkCreated = false;
        try
        {
            File.CreateSymbolicLink(link, secret);
            linkCreated = File.Exists(link);
        }
        catch
        {
            linkCreated = false; // no privilege / Developer Mode — skip the link half
        }

        Dictionary<string, string> map = AnnouncementModel.ReadEntryImagesAt(dir);
        Assert.True(map.ContainsKey("real.png")); // the genuine local image is served (non-vacuous)
        if (linkCreated)
        {
            // The link resolves OUTSIDE the entry folder -> excluded, no leak.
            Assert.False(map.ContainsKey("leak.png"));
        }
    }
}
