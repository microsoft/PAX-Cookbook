using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PAXCookbook.Shared;
using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbookSetup.Gui;

// The visible in-place update wizard (the `apply-update` verb). Launched by the
// running app's broker when the user clicks "Update now". It shows real
// progress while it downloads and installs the latest payload (reusing the same
// WizardInstallRunner path as a fresh GUI install), then a completion screen
// with exactly two choices:
//
//   [Open PAX Cookbook]  - launch the updated app, then close the updater.
//   [Close]              - leave the app closed; the user walks away.
//
// The app does NOT auto-reopen — the user decides. On failure the wizard shows a
// clear message with a single Close button. This runs in its OWN process
// (dotnet.exe Setup.dll apply-update), detached from the app, so when the
// install step stops the app to replace files this updater survives.
internal sealed class UpdateForm : Form
{
    private readonly string _installRoot;
    private readonly SetupLogger _log;

    public int ExitCode { get; private set; } = SetupExitCodes.Ok;

    private Panel _progressPanel = null!, _donePanel = null!, _errorPanel = null!;
    private ProgressBar _progressBar = null!;
    private Label _progressStatus = null!, _errorDetail = null!;
    private Button _btnOpen = null!, _btnCloseDone = null!, _btnCloseError = null!;
    private bool _running = true;

    public UpdateForm(string installRoot, SetupLogger log)
    {
        _installRoot = installRoot;
        _log = log;
        BuildForm();
    }

    private void BuildForm()
    {
        Text = "PAX Cookbook Updater";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 320);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5F);
        try { Icon = WizardAssets.LoadAppIcon(); } catch { /* default icon */ }

        _progressPanel = BuildProgressPanel();
        _donePanel = BuildDonePanel();
        _errorPanel = BuildErrorPanel();
        foreach (var p in new[] { _progressPanel, _donePanel, _errorPanel })
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            Controls.Add(p);
        }
        // Header added LAST so it reserves the top edge and the Fill panels take
        // the remainder (same z-order pattern as the other Setup dialogs).
        Controls.Add(BuildLogoHeader());
        _progressPanel.Visible = true;
        _progressPanel.BringToFront();

        Shown += (_, _) => StartUpdate();
    }

    private Panel BuildLogoHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Color.White };
        var rule = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(0xE0, 0xE0, 0xE0) };
        var pax = WizardAssets.CreatePaxLogo(new Point(20, 14), new Size(190, 34));
        var ms = WizardAssets.CreateMicrosoftLogo(new Point(384, 19), new Size(116, 22));
        header.Controls.Add(pax);
        header.Controls.Add(ms);
        header.Controls.Add(rule);
        return header;
    }

    private Panel BuildProgressPanel()
    {
        var p = new Panel { Padding = new Padding(24) };
        var heading = new Label
        {
            Text = "Updating PAX Cookbook\u2026",
            Location = new Point(24, 28), Size = new Size(470, 28),
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold)
        };
        _progressBar = new ProgressBar
        {
            Location = new Point(26, 74), Size = new Size(468, 22),
            Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30
        };
        _progressStatus = new Label
        {
            Text = "Starting\u2026",
            Location = new Point(26, 106), Size = new Size(468, 24),
            Font = new Font("Segoe UI", 10F)
        };
        var note = new Label
        {
            Text = "Do not close this window.",
            Location = new Point(26, 150), Size = new Size(468, 24),
            ForeColor = Color.FromArgb(0x60, 0x60, 0x60),
            Font = new Font("Segoe UI", 9.5F)
        };
        p.Controls.Add(heading);
        p.Controls.Add(_progressBar);
        p.Controls.Add(_progressStatus);
        p.Controls.Add(note);
        return p;
    }

    private Panel BuildDonePanel()
    {
        var p = new Panel { Padding = new Padding(24) };
        var heading = new Label
        {
            Text = "PAX Cookbook has been updated.",
            Location = new Point(24, 36), Size = new Size(470, 56),
            Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold)
        };
        var body = new Label
        {
            Text = "You can open PAX Cookbook now, or close this window and open it later.",
            Location = new Point(24, 96), Size = new Size(470, 44),
            Font = new Font("Segoe UI", 10F)
        };
        _btnOpen = new Button
        {
            Text = "Open PAX Cookbook", Size = new Size(170, 34), Location = new Point(206, 196)
        };
        _btnCloseDone = new Button
        {
            Text = "Close", Size = new Size(110, 34), Location = new Point(384, 196)
        };
        _btnOpen.Click += (_, _) => OpenAppAndClose();
        _btnCloseDone.Click += (_, _) => { ExitCode = SetupExitCodes.Ok; Close(); };
        p.Controls.Add(heading);
        p.Controls.Add(body);
        p.Controls.Add(_btnOpen);
        p.Controls.Add(_btnCloseDone);
        return p;
    }

    private Panel BuildErrorPanel()
    {
        var p = new Panel { Padding = new Padding(24) };
        var heading = new Label
        {
            Text = "The update couldn\u2019t be completed.",
            Location = new Point(24, 32), Size = new Size(470, 28),
            Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0xB0, 0x20, 0x20)
        };
        _errorDetail = new Label
        {
            Text = "",
            Location = new Point(24, 70), Size = new Size(470, 96),
            Font = new Font("Segoe UI", 10F)
        };
        _btnCloseError = new Button
        {
            Text = "Close", Size = new Size(110, 34), Location = new Point(384, 196)
        };
        _btnCloseError.Click += (_, _) => Close();
        p.Controls.Add(heading);
        p.Controls.Add(_errorDetail);
        p.Controls.Add(_btnCloseError);
        return p;
    }

    private void StartUpdate()
    {
        _log.Write("update-gui-start", fields: new Dictionary<string, object?>
        {
            ["installRoot"] = _installRoot
        });

        Action<string> progress = msg => BeginInvokeSafe(() => _progressStatus.Text = msg);

        Task.Run(async () =>
        {
            WizardInstallResult result;
            try
            {
                // 1. Stop ALL PAX Cookbook processes (broker daemon + window) and
                //    do not continue until they are GONE and App\bin is actually
                //    writable. A still-running app holding App\bin is what
                //    produced "Installation failed (exit code 50)"; this
                //    re-scans + force-kills and then waits for the file lock to
                //    release, so the copy can never hit a locked file. The app
                //    also hard-exits itself when it launches us (belt and
                //    suspenders).
                UpdateAppStop.WaitUntilClear(_installRoot, _log, progress);

                var shellOps = ShellOperationsFactory.Build();
                // Reuses the proven fresh-install path: download the latest
                // payload from GitHub (verified against versions.json), then copy
                // + verify the new files (the app is already stopped above).
                // Progress callbacks drive the status line ("Downloading\u2026",
                // "Verifying download\u2026", "Installing\u2026", "Finishing up\u2026").
                result = await WizardInstallRunner.RunAsync(
                    _installRoot, payloadRootOverride: null, progress, _log, shellOps);
            }
            catch (Exception ex)
            {
                _log.Write("update-gui-exception", "error",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
                result = new WizardInstallResult(false, SetupExitCodes.InstallFailed, ex.Message);
            }
            BeginInvokeSafe(() => OnFinished(result));
        });
    }

    private void OnFinished(WizardInstallResult result)
    {
        _running = false;
        ExitCode = result.ExitCode;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = 100;

        if (result.Success)
        {
            _log.Write("update-gui-success");
            _progressPanel.Visible = false;
            _donePanel.Visible = true;
            _donePanel.BringToFront();
            _btnOpen.Focus();
        }
        else
        {
            _log.Write("update-gui-failed", "error",
                new Dictionary<string, object?> { ["detail"] = result.Error });
            _errorDetail.Text = string.IsNullOrWhiteSpace(result.Error)
                ? "Make sure you are online and try again. Your installed copy of PAX Cookbook was not changed."
                : result.Error + "\n\nMake sure you are online and try again. Your installed copy of " +
                  "PAX Cookbook was not changed.";
            _progressPanel.Visible = false;
            _errorPanel.Visible = true;
            _errorPanel.BringToFront();
            _btnCloseError.Focus();
        }
    }

    // Launch the updated app the same WDAC-safe way the installer's shell
    // integration does: the Microsoft-signed dotnet.exe host runs the app DLL.
    private void OpenAppAndClose()
    {
        try
        {
            var dotnet = DotNetLaunch.DotNetExePath();
            var dll = DotNetLaunch.AppDllPath(_installRoot);
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(dll) ?? _installRoot,
            };
            psi.ArgumentList.Add(dll);
            Process.Start(psi);
            _log.Write("update-gui-open-app");
        }
        catch (Exception ex)
        {
            _log.Write("update-gui-open-app-failed", "warn",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
        }
        ExitCode = SetupExitCodes.Ok;
        Close();
    }

    private void BeginInvokeSafe(Action action)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
        catch { /* form closing */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Do not allow closing while the download / file copy is in progress.
        if (_running) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }
}
