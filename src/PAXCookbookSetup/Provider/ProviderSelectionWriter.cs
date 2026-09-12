using System;
using System.IO;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provider;

// Transactional writer for the selected-session-provider record.
//
// The provider selection is always written LAST, after every target-provider
// prerequisite has been validated, so a failure before the write leaves the
// prior selection untouched (no partial selection is ever persisted). This type
// also snapshots the prior record so an explicit rollback (e.g. the user
// cancels after we changed something, or a later staged step fails) restores the
// exact prior state — including "no record existed".
//
// It owns ONLY the non-secret provider selection record. WAM durable config and
// verification are written by the guarded helper / experimental app, never here.
public sealed class ProviderSelectionWriter
{
    private readonly string _localAppDataBase;
    private bool _snapshotTaken;
    private bool _priorExisted;
    private byte[] _priorBytes = Array.Empty<byte>();

    public ProviderSelectionWriter(string localAppDataBase)
    {
        _localAppDataBase = localAppDataBase
            ?? throw new ArgumentNullException(nameof(localAppDataBase));
    }

    public string SelectionPath => SessionProviderStore.ResolveSelectionPath(_localAppDataBase);

    // Captures the prior record (bytes or absence) so a later Restore is exact.
    public void Snapshot()
    {
        string path = SelectionPath;
        if (File.Exists(path))
        {
            _priorExisted = true;
            _priorBytes = File.ReadAllBytes(path);
        }
        else
        {
            _priorExisted = false;
            _priorBytes = Array.Empty<byte>();
        }
        _snapshotTaken = true;
    }

    // Writes the selection atomically (shared store: temp + replace). This is the
    // FINAL commit step of a provider change; nothing target-provider-specific
    // should run after it.
    public void WriteSelectionLast(SelectedSessionProvider provider, ProviderSelectionSource source)
        => SessionProviderStore.Save(_localAppDataBase, provider, source);

    // Restores the exact prior record captured by Snapshot: rewrites the prior
    // bytes, or deletes the file if none existed. No-op if no snapshot was taken.
    public void Restore()
    {
        if (!_snapshotTaken)
        {
            return;
        }

        string path = SelectionPath;
        if (_priorExisted)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, _priorBytes);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
