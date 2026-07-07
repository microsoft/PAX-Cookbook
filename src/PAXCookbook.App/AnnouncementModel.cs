using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// "What's New" release-history mechanism (feature D — see
// _temp/service_mode_plan/D_announcement_popup.md).
//
// A newsletter-style What's New history for non-technical users. Each release
// MAY ship ONE new entry in its payload under <appRoot>\announcements\<id>\
// (entry.json + body.md + local images). Because an update overlays the payload
// (older releases' entry folders are not guaranteed to survive), entries are
// COPIED into a per-user archive the moment they are first encountered, so the
// full history stays browsable long after later updates replace the shipped
// copy. The archive lives at %APPDATA%\PAXCookbook\announcements\ with an
// index.json + one subfolder per entry.
//
// TRIGGER GATES (unchanged from the single-marker design): archiving AND the
// startup auto-popup are gated on install-state.json LastOperation.Kind ==
// "update" && Status == "ok". A fresh Setup install (Kind == "install") or a
// manual-kit install (no install-state.json) NEVER archives and NEVER
// auto-shows. The on-demand "What's New" browser reads the archive directly and
// works any time (it is simply empty until an update has archived something).
//
// PER-ENTRY show-again: each entry carries its own showAtStartup flag (default
// true when first archived). The startup popup opens to the NEWEST entry and
// appears only while that newest entry's flag is true; unchecking "show again"
// for an entry is a one-way per-entry dismiss (the on-demand browser still
// reaches it). A different, newer entry always starts fresh at show=true,
// independent of any prior entry's choice — never a global on/off.
//
// IMAGES are delivered as sandboxed data: URIs: BuildHistory reads only the
// top-level allowed-extension image files physically present in an entry's own
// archive folder and returns { filename -> dataUri }. The safe Markdown renderer
// resolves ![alt](name) ONLY against that map, so a remote URL or a
// ../traversal reference is not a map key and renders as alt text (no fetch, no
// escape from the entry folder).
//
// All file paths are injectable (the *At overloads) so the whole flow is unit-
// testable against temp fixtures without touching the real install.
internal static class AnnouncementModel
{
    // Payload/archive subfolder name (relative to appRoot for the shipped copy;
    // the archive root has the same leaf name under %APPDATA%\PAXCookbook).
    internal const string AnnouncementsFolder = "announcements";
    internal const string EntryManifestFile = "entry.json";
    internal const string DefaultBodyFile = "body.md";
    internal const string IndexFile = "index.json";

    private const string ArchiveParentFolder = "PAXCookbook";

    // Keep at most this many entries in the per-user archive (newest first);
    // older entries are pruned. Announcements are small + infrequent, so this is
    // generous — bounded, not tuned. (Open: make configurable if ever needed.)
    internal const int RetentionMax = 50;

    // A stable, author-set entry id. Constrained so it is always a safe single
    // path segment (no separators, no traversal) — it becomes a folder name.
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    // Only these image types are ever surfaced (SVG is intentionally excluded —
    // it can carry script).
    private static readonly Dictionary<string, string> ImageMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ---------------------------------------------------------------------
    // Public GET handler. Archives any newly-shipped entry (only when the last
    // operation was a successful update), then returns the full history newest
    // first plus the autoShow decision. Read-mostly (the sync is idempotent).
    // ---------------------------------------------------------------------
    public static (int Status, object Body) GetHistory(string appRoot)
    {
        (string? kind, string? status, _) = ReadInstallOperationAt(InstallStatePathFor(appRoot));
        SyncEntriesAt(PayloadEntriesDirFor(appRoot), DefaultArchiveRoot(), kind, status);
        return (200, BuildHistoryAt(DefaultArchiveRoot(), kind, status));
    }

    // Public POST handler. Records the per-entry "show this message again when I
    // open Cookbook" choice. body { id: string, showAgain: bool }.
    public static (int Status, object Body) SetShowAgain(object? body, string appRoot)
    {
        if (body is not Dictionary<string, object?> request)
        {
            return (400, new { error = "invalid_json" });
        }
        if (!request.TryGetValue("id", out object? rawId) || rawId is not string id || !SafeId.IsMatch(id))
        {
            return (400, new { error = "validation_failed", reason = "id_required", field = "id" });
        }
        if (!request.TryGetValue("showAgain", out object? raw) || raw is not bool showAgain)
        {
            return (400, new { error = "validation_failed", reason = "showAgain_required", field = "showAgain" });
        }
        return SetShowAgainAt(DefaultArchiveRoot(), id, showAgain);
    }

    // ---------------------------------------------------------------------
    // Path resolution.
    // ---------------------------------------------------------------------
    internal static string InstallStatePathFor(string appRoot)
    {
        string? installRoot = Path.GetDirectoryName(Path.GetFullPath(appRoot));
        installRoot ??= Path.GetFullPath(appRoot);
        return Path.Combine(installRoot, "install-state.json");
    }

    // Where a release ships its ONE new entry (subfolders per entry id).
    internal static string PayloadEntriesDirFor(string appRoot)
        => Path.Combine(Path.GetFullPath(appRoot), AnnouncementsFolder);

    // The persistent per-user archive that survives future updates.
    internal static string DefaultArchiveRoot()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ArchiveParentFolder, AnnouncementsFolder);
    }

    // ---------------------------------------------------------------------
    // Archive sync. Copies newly-shipped (or content-corrected) entries from the
    // payload into the per-user archive. NO-OP unless the last operation was a
    // successful update — so a fresh install never archives anything.
    // ---------------------------------------------------------------------
    internal static void SyncEntriesAt(string payloadEntriesDir, string archiveRoot, string? kind, string? status)
    {
        // gate 1 — never on a fresh install / manual-kit install.
        if (!string.Equals(kind, "update", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            if (!Directory.Exists(payloadEntriesDir))
            {
                return;
            }
            List<IndexEntry> index = ReadIndexAt(archiveRoot);
            bool changed = false;

            foreach (string entryDir in Directory.EnumerateDirectories(payloadEntriesDir))
            {
                ShippedEntry? shipped = ReadShippedEntry(entryDir);
                if (shipped is null)
                {
                    continue;
                }
                string contentHash = ComputeEntryContentHash(entryDir, shipped.BodyFile);
                IndexEntry? existing = index.FirstOrDefault(e => string.Equals(e.Id, shipped.Id, StringComparison.Ordinal));
                if (existing is null)
                {
                    CopyEntryInto(entryDir, Path.Combine(archiveRoot, shipped.Id), shipped);
                    index.Add(new IndexEntry
                    {
                        Id = shipped.Id,
                        Title = shipped.Title,
                        Date = shipped.Date,
                        ArchivedAtUtc = DateTime.UtcNow.ToString("o"),
                        ShowAtStartup = true, // a brand-new entry always starts shown
                        ContentHash = contentHash,
                    });
                    changed = true;
                }
                else if (!string.Equals(existing.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Same id, corrected content: refresh the archived files and
                    // metadata but KEEP its position (archivedAtUtc) and the
                    // user's show-again choice — the entry keeps its place.
                    CopyEntryInto(entryDir, Path.Combine(archiveRoot, existing.Id), shipped);
                    existing.Title = shipped.Title;
                    existing.Date = shipped.Date;
                    existing.ContentHash = contentHash;
                    changed = true;
                }
            }

            if (changed)
            {
                PruneToRetention(index, archiveRoot);
                WriteIndexAt(archiveRoot, index);
            }
        }
        catch
        {
            // Best-effort: a sync failure just means an entry is not archived
            // this run; it never blocks the app.
        }
    }

    // ---------------------------------------------------------------------
    // Build the browsable history (newest first) + the autoShow decision.
    // ---------------------------------------------------------------------
    internal static object BuildHistoryAt(string archiveRoot, string? kind, string? status)
    {
        List<IndexEntry> index = ReadIndexAt(archiveRoot);
        List<IndexEntry> ordered = index
            .OrderByDescending(e => e.Date ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(e => e.ArchivedAtUtc ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var entries = new List<object>();
        foreach (IndexEntry e in ordered)
        {
            string entryFolder = Path.Combine(archiveRoot, e.Id);
            string body = ReadBodyAt(entryFolder, e.Id);
            Dictionary<string, string> images = ReadEntryImagesAt(entryFolder);
            entries.Add(new
            {
                id = e.Id,
                title = e.Title,
                date = e.Date,
                showAgain = e.ShowAtStartup,
                bodyMarkdown = body,
                images,
            });
        }

        bool gate1 = string.Equals(kind, "update", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
        string? newestId = ordered.Count > 0 ? ordered[0].Id : null;
        // The startup popup opens to the newest entry, and only while that newest
        // entry is still "show again". Older still-checked entries do not
        // resurrect the popup once the newest is dismissed (design choice —
        // flagged in the plan doc).
        bool autoShow = gate1 && ordered.Count > 0 && ordered[0].ShowAtStartup;

        return new
        {
            entries,
            autoShow,
            newestId,
        };
    }

    internal static (int Status, object Body) SetShowAgainAt(string archiveRoot, string id, bool showAgain)
    {
        if (!SafeId.IsMatch(id))
        {
            return (400, new { error = "validation_failed", reason = "invalid_id" });
        }
        List<IndexEntry> index = ReadIndexAt(archiveRoot);
        IndexEntry? entry = index.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (entry is null)
        {
            return (404, new { error = "not_found", id });
        }
        entry.ShowAtStartup = showAgain;
        WriteIndexAt(archiveRoot, index);
        return (200, new { ok = true, id, showAgain });
    }

    // ---------------------------------------------------------------------
    // Shipped-entry reading + copy.
    // ---------------------------------------------------------------------
    private sealed record ShippedEntry(string Id, string Title, string? Date, string BodyFile);

    private static ShippedEntry? ReadShippedEntry(string entryDir)
    {
        try
        {
            string manifest = Path.Combine(entryDir, EntryManifestFile);
            if (!File.Exists(manifest))
            {
                return null;
            }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            JsonElement root = doc.RootElement;
            string? id = GetStringCI(root, "id");
            string? title = GetStringCI(root, "title");
            string? date = GetStringCI(root, "date");
            string? bodyFile = GetStringCI(root, "bodyFile");
            if (string.IsNullOrEmpty(id) || !SafeId.IsMatch(id) || string.IsNullOrEmpty(title))
            {
                return null;
            }
            // bodyFile must be a plain filename in the entry folder (no traversal).
            if (string.IsNullOrEmpty(bodyFile) || bodyFile.Contains('/') || bodyFile.Contains('\\') || bodyFile.Contains(".."))
            {
                bodyFile = DefaultBodyFile;
            }
            return new ShippedEntry(id!, title!, date, bodyFile!);
        }
        catch
        {
            return null;
        }
    }

    private static void CopyEntryInto(string sourceEntryDir, string destEntryDir, ShippedEntry shipped)
    {
        Directory.CreateDirectory(destEntryDir);
        string srcFull = Path.GetFullPath(sourceEntryDir);
        // Copy entry.json, the body file, and top-level allowed-extension images
        // ONLY — never subdirectories or other file types, and never a file that
        // (via a link) resolves outside the source entry folder.
        foreach (string src in Directory.EnumerateFiles(sourceEntryDir, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(src);
            bool allowed =
                string.Equals(name, EntryManifestFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, shipped.BodyFile, StringComparison.OrdinalIgnoreCase) ||
                ImageMime.ContainsKey(Path.GetExtension(name));
            if (allowed && ResolvesInsideFolder(src, srcFull))
            {
                File.Copy(src, Path.Combine(destEntryDir, name), overwrite: true);
            }
        }
    }

    private static string ComputeEntryContentHash(string entryDir, string bodyFile)
    {
        using var sha = SHA256.Create();
        void Fold(byte[] b) => sha.TransformBlock(b, 0, b.Length, null, 0);
        try
        {
            string bodyPath = Path.Combine(entryDir, bodyFile);
            if (File.Exists(bodyPath)) { Fold(File.ReadAllBytes(bodyPath)); }
            foreach (string img in Directory.EnumerateFiles(entryDir, "*", SearchOption.TopDirectoryOnly)
                         .Where(f => ImageMime.ContainsKey(Path.GetExtension(f)))
                         .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                Fold(Encoding.UTF8.GetBytes(Path.GetFileName(img)));
                Fold(File.ReadAllBytes(img));
            }
        }
        catch { /* partial hash is fine — a read error just yields a different hash */ }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    // ---------------------------------------------------------------------
    // Body + image reading from a SANDBOXED entry folder.
    // ---------------------------------------------------------------------
    private static string ReadBodyAt(string entryFolder, string id)
    {
        try
        {
            // Prefer the bodyFile named in the archived entry.json; fall back to
            // the default. Both are plain filenames inside the entry folder.
            string? bodyFile = DefaultBodyFile;
            string manifest = Path.Combine(entryFolder, EntryManifestFile);
            if (File.Exists(manifest))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
                string? bf = GetStringCI(doc.RootElement, "bodyFile");
                if (!string.IsNullOrEmpty(bf) && !bf.Contains('/') && !bf.Contains('\\') && !bf.Contains(".."))
                {
                    bodyFile = bf;
                }
            }
            string bodyPath = Path.Combine(entryFolder, bodyFile!);
            return File.Exists(bodyPath) ? File.ReadAllText(bodyPath) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Returns { filename -> data:uri } for the top-level allowed-extension images
    // physically present in the entry folder. This is the ONLY image source the
    // renderer trusts, so remote/traversal references simply never match.
    // Hardening: a single unreadable/corrupt file is skipped (never aborts the
    // set), and a file that resolves (via a symlink/junction) OUTSIDE the entry
    // folder is excluded — the sandbox follows real paths, not links.
    internal static Dictionary<string, string> ReadEntryImagesAt(string entryFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(entryFolder))
            {
                return map;
            }
            string folderFull = Path.GetFullPath(entryFolder);
            foreach (string file in Directory.EnumerateFiles(entryFolder, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string ext = Path.GetExtension(file);
                    if (!ImageMime.TryGetValue(ext, out string? mime))
                    {
                        continue;
                    }
                    // Sandbox: never read a file that (via a link) resolves outside
                    // the entry's own folder.
                    if (!ResolvesInsideFolder(file, folderFull))
                    {
                        continue;
                    }
                    byte[] bytes = File.ReadAllBytes(file);
                    map[Path.GetFileName(file)] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                }
                catch
                {
                    // Skip a single unreadable / corrupt / mid-flight file; the
                    // rest of the set still loads (graceful, never throws up).
                }
            }
        }
        catch
        {
            // fall through to whatever was gathered
        }
        return map;
    }

    // True only when `filePath`, after resolving any symlink/junction to its real
    // final target, still lives directly under `folderFull`. A normal file
    // resolves to itself (inside); a link pointing outside resolves outside and
    // is rejected. Any resolution failure is treated as "outside" (fail-closed).
    private static bool ResolvesInsideFolder(string filePath, string folderFull)
    {
        try
        {
            var info = new FileInfo(filePath);
            FileSystemInfo? finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
            string real = Path.GetFullPath(finalTarget?.FullName ?? filePath);
            string prefix = folderFull.EndsWith(Path.DirectorySeparatorChar)
                ? folderFull
                : folderFull + Path.DirectorySeparatorChar;
            return real.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Index (archive metadata) read/write + retention pruning.
    // ---------------------------------------------------------------------
    internal sealed class IndexEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Date { get; set; }
        public string? ArchivedAtUtc { get; set; }
        public bool ShowAtStartup { get; set; } = true;
        public string? ContentHash { get; set; }
    }

    internal static List<IndexEntry> ReadIndexAt(string archiveRoot)
    {
        var list = new List<IndexEntry>();
        try
        {
            string indexPath = Path.Combine(archiveRoot, IndexFile);
            if (!File.Exists(indexPath))
            {
                return list;
            }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("entries", out JsonElement entries) &&
                entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in entries.EnumerateArray())
                {
                    string? id = GetStringCI(e, "id");
                    if (string.IsNullOrEmpty(id) || !SafeId.IsMatch(id))
                    {
                        continue;
                    }
                    bool show = !(e.TryGetProperty("showAtStartup", out JsonElement s) && s.ValueKind == JsonValueKind.False);
                    list.Add(new IndexEntry
                    {
                        Id = id!,
                        Title = GetStringCI(e, "title") ?? id!,
                        Date = GetStringCI(e, "date"),
                        ArchivedAtUtc = GetStringCI(e, "archivedAtUtc"),
                        ShowAtStartup = show,
                        ContentHash = GetStringCI(e, "contentHash"),
                    });
                }
            }
        }
        catch
        {
            // A corrupt index resolves to empty (no history shown) rather than
            // throwing — the archive folders are still on disk and re-sync can
            // rebuild the index on the next update.
        }
        return list;
    }

    internal static void WriteIndexAt(string archiveRoot, List<IndexEntry> index)
    {
        try
        {
            Directory.CreateDirectory(archiveRoot);
            var payload = new
            {
                schemaVersion = 1,
                updatedAtUtc = DateTime.UtcNow.ToString("o"),
                entries = index.Select(e => new
                {
                    id = e.Id,
                    title = e.Title,
                    date = e.Date,
                    archivedAtUtc = e.ArchivedAtUtc,
                    showAtStartup = e.ShowAtStartup,
                    contentHash = e.ContentHash,
                }).ToList(),
            };
            string json = JsonSerializer.Serialize(payload, JsonOpts);
            string indexPath = Path.Combine(archiveRoot, IndexFile);
            string tmp = indexPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(indexPath)) { File.Delete(indexPath); }
            File.Move(tmp, indexPath);
        }
        catch
        {
            // Best-effort persist.
        }
    }

    // Keep only the newest RetentionMax entries; delete pruned entries' folders.
    private static void PruneToRetention(List<IndexEntry> index, string archiveRoot)
    {
        if (index.Count <= RetentionMax)
        {
            return;
        }
        List<IndexEntry> ordered = index
            .OrderByDescending(e => e.Date ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(e => e.ArchivedAtUtc ?? string.Empty, StringComparer.Ordinal)
            .ToList();
        foreach (IndexEntry stale in ordered.Skip(RetentionMax))
        {
            index.Remove(stale);
            try
            {
                string folder = Path.Combine(archiveRoot, stale.Id);
                if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
            }
            catch { /* leaving a stale folder is harmless */ }
        }
    }

    // ---------------------------------------------------------------------
    // install-state.json reader (Setup writes, App reads) — camelCase, parsed
    // directly so the App carries no dependency on the Setup contract assembly.
    // ---------------------------------------------------------------------
    internal static (string? Kind, string? Status, string? UpdatedAtUtc) ReadInstallOperationAt(string installStatePath)
    {
        try
        {
            if (!File.Exists(installStatePath))
            {
                return (null, null, null);
            }
            string json = File.ReadAllText(installStatePath);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? updatedAt = GetStringCI(root, "updatedAtUtc");
            string? kind = null;
            string? status = null;
            if (TryGetPropertyCI(root, "lastOperation", out JsonElement lastOp) &&
                lastOp.ValueKind == JsonValueKind.Object)
            {
                kind = GetStringCI(lastOp, "kind");
                status = GetStringCI(lastOp, "status");
            }
            return (kind, status, updatedAt);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetStringCI(JsonElement obj, string name)
        => TryGetPropertyCI(obj, name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
