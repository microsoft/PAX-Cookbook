using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PAXCookbook.Shared;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Shell;

namespace PAXCookbookSetup.Gui;

// The PAX Cookbook GUI installer. Five panel-based screens (Welcome,
// Prerequisites, Location, Progress, Complete) inside one fixed-size form,
// with Back/Next/Cancel navigation. This slice builds the framework and the
// real PAX Cookbook install (Progress -> Complete); it DISPLAYS prerequisite
// detection but does not yet download/install PowerShell 7 or Python (wired
// in a later slice). The verb-based CLI remains the path for scripted/silent
// installs.
internal sealed class SetupWizardForm : Form
{
    private enum Step { Welcome = 0, Prerequisites = 1, Location = 2, Progress = 3, Complete = 4 }

    private readonly SetupLogger _log;
    private readonly IShellOperations _shellOps;
    private readonly PrerequisiteDetector _detector;
    private readonly string _defaultInstallRoot;

    private Step _step = Step.Welcome;
    private string _installRoot;
    private bool _installRunning;
    private bool _installFailed;
    public int ExitCode { get; private set; } = SetupExitCodes.Ok;

    // Header
    private Panel _header = null!;
    private PictureBox _logoBox = null!;
    private PictureBox _msLogoBox = null!;
    private Label _headerTitle = null!;

    // Footer
    private Button _btnBack = null!, _btnNext = null!, _btnCancel = null!;

    // Screen panels
    private Panel _panelWelcome = null!, _panelPrereq = null!, _panelLocation = null!,
                  _panelProgress = null!, _panelComplete = null!;

    // Prerequisites screen
    private Label _prereqHeading = null!, _dotnet8Line = null!, _ps7Line = null!, _pyLine = null!, _prereqIntro = null!, _prereqNote = null!;
    private CheckBox _chkInstallPs7 = null!, _chkInstallPy = null!;
    private PrerequisiteStatus? _dotnet8Status, _ps7Status, _pyStatus;

    // Location screen
    private TextBox _txtPath = null!;
    private Label _freeSpaceLabel = null!;

    // Progress screen
    private ProgressBar _progressBar = null!;
    private Label _progressStatus = null!;
    private TextBox _progressLog = null!;

    // Complete screen
    private Label _completeMsg = null!;
    private Label _prereqWarning = null!;
    private CheckBox _chkLaunch = null!, _chkAutoStart = null!;
    private IReadOnlyList<NamedPrerequisiteResult> _prereqResults = Array.Empty<NamedPrerequisiteResult>();

    public SetupWizardForm(string installRoot, SetupLogger log, IShellOperations shellOps,
                           PrerequisiteDetector detector)
    {
        _defaultInstallRoot = installRoot;
        _installRoot = installRoot;
        _log = log;
        _shellOps = shellOps;
        _detector = detector;

        BuildForm();
        ShowStep(Step.Welcome);
    }

    // -----------------------------------------------------------------
    // Form / layout construction
    // -----------------------------------------------------------------
    private void BuildForm()
    {
        Text = "PAX Cookbook Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 500);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);
        try { Icon = WizardAssets.LoadAppIcon(); } catch { /* default icon */ }

        BuildHeader();
        BuildFooter();

        _panelWelcome = BuildWelcomePanel();
        _panelPrereq = BuildPrereqPanel();
        _panelLocation = BuildLocationPanel();
        _panelProgress = BuildProgressPanel();
        _panelComplete = BuildCompletePanel();

        foreach (var p in new[] { _panelWelcome, _panelPrereq, _panelLocation, _panelProgress, _panelComplete })
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            Controls.Add(p);
        }
        // Header and footer are added last so they keep their docked edges
        // above the fill panels in z-order.
        Controls.Add(_header);
        Controls.Add(_footerPanel);
    }

    private Panel _footerPanel = null!;

    private void BuildHeader()
    {
        _header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Color.White };
        var rule = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(0xE0, 0xE0, 0xE0) };
        // PAX Cookbook brand logo, top-left, clickable -> PAXcookbook.com. Shown
        // on every screen.
        _logoBox = WizardAssets.CreatePaxLogo(new Point(24, 16), new Size(320, 60));
        // Microsoft Open Source logo, top-right, clickable -> opensource.microsoft.com.
        // Smaller (secondary) but legible; shown on every screen.
        _msLogoBox = WizardAssets.CreateMicrosoftLogo(new Point(520, 34), new Size(136, 26));
        _headerTitle = new Label
        {
            AutoSize = false,
            Location = new Point(24, 0),
            Size = new Size(632, 96),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x0A, 0x1F, 0x44),
            Visible = false
        };
        _header.Controls.Add(_logoBox);
        _header.Controls.Add(_msLogoBox);
        _header.Controls.Add(_headerTitle);
        _header.Controls.Add(rule);
    }

    private void BuildFooter()
    {
        _footerPanel = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(0xF5, 0xF6, 0xF8) };
        var rule = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(0xE0, 0xE0, 0xE0) };
        _btnCancel = new Button { Text = "Cancel", Size = new Size(96, 30), Location = new Point(560, 13) };
        _btnNext = new Button { Text = "Next", Size = new Size(96, 30), Location = new Point(456, 13) };
        _btnBack = new Button { Text = "Back", Size = new Size(96, 30), Location = new Point(352, 13) };
        _btnCancel.Click += (_, _) => OnCancel();
        _btnNext.Click += (_, _) => OnNext();
        _btnBack.Click += (_, _) => OnBack();
        _footerPanel.Controls.Add(_btnCancel);
        _footerPanel.Controls.Add(_btnNext);
        _footerPanel.Controls.Add(_btnBack);
        _footerPanel.Controls.Add(rule);
    }

    private static Label Body(string text, int x, int y, int w, int h, float size = 9.75F, FontStyle style = FontStyle.Regular)
        => new()
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            Font = new Font("Segoe UI", size, style),
            ForeColor = Color.FromArgb(0x20, 0x20, 0x20)
        };

    private Panel BuildWelcomePanel()
    {
        var p = new Panel { Padding = new Padding(28, 24, 28, 16) };
        p.Controls.Add(Body("Welcome to PAX Cookbook Setup", 28, 28, 600, 36, 17F, FontStyle.Bold));
        p.Controls.Add(Body($"Version v{DisplayVersion()}", 28, 74, 600, 24, 10.5F));
        p.Controls.Add(Body(
            "PAX Cookbook helps you pull Microsoft 365 Copilot adoption and usage data " +
            "into Power BI dashboards.\n\nThis wizard will install PAX Cookbook on your PC " +
            "and check for the tools it needs.\n\nClick Next to continue.",
            28, 116, 612, 170, 10.5F));
        return p;
    }

    private Panel BuildPrereqPanel()
    {
        var p = new Panel { Padding = new Padding(28, 24, 28, 16) };
        _prereqHeading = Body("Checking prerequisites…", 28, 24, 612, 28, 11F, FontStyle.Bold);
        _dotnet8Line = Body("• .NET 8 Desktop Runtime: checking…", 40, 70, 600, 24);
        _ps7Line = Body("• PowerShell 7: checking…", 40, 100, 600, 24);
        _pyLine = Body("• Python: checking…", 40, 130, 600, 24);
        _prereqIntro = Body("", 28, 146, 612, 24, 10F, FontStyle.Bold);
        _chkInstallPs7 = new CheckBox { Text = "Install PowerShell 7 automatically", Location = new Point(44, 176), Size = new Size(560, 24), Checked = true, Visible = false };
        _chkInstallPy = new CheckBox { Text = "Install Python automatically", Location = new Point(44, 204), Size = new Size(560, 24), Checked = true, Visible = false };
        _prereqNote = Body(
            "Note: installing PowerShell 7 may require administrator approval. PAX Cookbook " +
            "itself installs for your user account only and does not require administrator rights.",
            28, 250, 612, 60, 9F);
        _prereqNote.ForeColor = Color.FromArgb(0x60, 0x60, 0x60);
        p.Controls.Add(_prereqHeading);
        p.Controls.Add(_dotnet8Line);
        p.Controls.Add(_ps7Line);
        p.Controls.Add(_pyLine);
        p.Controls.Add(_prereqIntro);
        p.Controls.Add(_chkInstallPs7);
        p.Controls.Add(_chkInstallPy);
        p.Controls.Add(_prereqNote);
        return p;
    }

    private Panel BuildLocationPanel()
    {
        var p = new Panel { Padding = new Padding(28, 24, 28, 16) };
        p.Controls.Add(Body("Choose install location", 28, 24, 612, 28, 13F, FontStyle.Bold));
        p.Controls.Add(Body("PAX Cookbook will be installed in the following folder:", 28, 70, 612, 24, 10F));
        _txtPath = new TextBox { Location = new Point(30, 100), Size = new Size(520, 26), Text = _defaultInstallRoot };
        var browse = new Button { Text = "Browse…", Location = new Point(558, 99), Size = new Size(90, 28) };
        browse.Click += (_, _) => OnBrowse();
        _freeSpaceLabel = Body("", 28, 140, 612, 24, 9.5F);
        _freeSpaceLabel.ForeColor = Color.FromArgb(0x60, 0x60, 0x60);
        p.Controls.Add(_txtPath);
        p.Controls.Add(browse);
        p.Controls.Add(_freeSpaceLabel);
        p.Controls.Add(Body("Click Install to begin.", 28, 176, 612, 24, 10F));
        return p;
    }

    private Panel BuildProgressPanel()
    {
        var p = new Panel { Padding = new Padding(28, 24, 28, 16) };
        p.Controls.Add(Body("Installing PAX Cookbook", 28, 24, 612, 28, 13F, FontStyle.Bold));
        _progressBar = new ProgressBar { Location = new Point(30, 70), Size = new Size(618, 22), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
        _progressStatus = Body("Starting…", 30, 102, 618, 24, 10F);
        _progressLog = new TextBox
        {
            Location = new Point(30, 134), Size = new Size(618, 170),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(0xFB, 0xFB, 0xFC), Font = new Font("Consolas", 9F)
        };
        p.Controls.Add(_progressBar);
        p.Controls.Add(_progressStatus);
        p.Controls.Add(_progressLog);
        return p;
    }

    private Panel BuildCompletePanel()
    {
        var p = new Panel { Padding = new Padding(28, 24, 28, 16) };
        p.Controls.Add(Body("Installation complete", 28, 24, 612, 32, 15F, FontStyle.Bold));
        _completeMsg = Body("PAX Cookbook has been installed successfully.", 28, 72, 612, 28, 10.5F);
        _prereqWarning = Body("", 28, 104, 612, 56, 9F);
        _prereqWarning.ForeColor = Color.FromArgb(0x9A, 0x6A, 0x00);
        _chkLaunch = new CheckBox { Text = "Launch PAX Cookbook now", Location = new Point(30, 172), Size = new Size(600, 24), Checked = true };
        _chkAutoStart = new CheckBox { Text = "Start PAX Cookbook at login (recommended for scheduled bakes)", Location = new Point(30, 202), Size = new Size(620, 24), Checked = true };
        p.Controls.Add(_completeMsg);
        p.Controls.Add(_prereqWarning);
        p.Controls.Add(_chkLaunch);
        p.Controls.Add(_chkAutoStart);
        return p;
    }

    // -----------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------
    private void ShowStep(Step step)
    {
        _step = step;
        _panelWelcome.Visible = step == Step.Welcome;
        _panelPrereq.Visible = step == Step.Prerequisites;
        _panelLocation.Visible = step == Step.Location;
        _panelProgress.Visible = step == Step.Progress;
        _panelComplete.Visible = step == Step.Complete;

        // Header: the PAX Cookbook logo (top-left) and the Microsoft Open Source
        // logo (top-right) both show on every screen; each panel supplies its own
        // heading text, so the header text title is no longer used.
        _logoBox.Visible = true;
        _headerTitle.Visible = false;
        _headerTitle.Text = step switch
        {
            Step.Prerequisites => "Prerequisites",
            Step.Location => "Install location",
            Step.Progress => "Installing",
            _ => ""
        };

        switch (step)
        {
            case Step.Welcome:
                SetButtons(back: false, nextText: "Next", cancel: true);
                break;
            case Step.Prerequisites:
                SetButtons(back: true, nextText: "Next", cancel: true);
                StartDetection();
                break;
            case Step.Location:
                SetButtons(back: true, nextText: "Install", cancel: true);
                UpdateFreeSpace();
                break;
            case Step.Progress:
                SetButtons(back: false, nextText: "Next", cancel: false);
                _btnNext.Visible = false;
                break;
            case Step.Complete:
                SetButtons(back: false, nextText: "Finish", cancel: false);
                break;
        }
        var active = step switch
        {
            Step.Welcome => _panelWelcome,
            Step.Prerequisites => _panelPrereq,
            Step.Location => _panelLocation,
            Step.Progress => _panelProgress,
            _ => _panelComplete
        };
        active.BringToFront();
    }

    private void SetButtons(bool back, string nextText, bool cancel)
    {
        _btnBack.Visible = back;
        _btnNext.Visible = true;
        _btnNext.Text = nextText;
        _btnNext.Enabled = true;
        _btnCancel.Visible = cancel;
    }

    private void OnNext()
    {
        switch (_step)
        {
            case Step.Welcome: ShowStep(Step.Prerequisites); break;
            case Step.Prerequisites: ShowStep(Step.Location); break;
            case Step.Location: BeginInstall(); break;
            case Step.Complete: FinishAndClose(); break;
        }
    }

    private void OnBack()
    {
        switch (_step)
        {
            case Step.Prerequisites: ShowStep(Step.Welcome); break;
            case Step.Location: ShowStep(Step.Prerequisites); break;
        }
    }

    private void OnCancel()
    {
        if (_installRunning) return;
        // After a failed install the Cancel button is relabelled "Close":
        // close directly, preserving the failure exit code already set in
        // OnInstallFinished (no "Cancel Setup?" prompt — there is nothing
        // left to cancel).
        if (_installFailed) { Close(); return; }
        var r = MessageBox.Show("Cancel PAX Cookbook Setup?", "PAX Cookbook Setup",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes)
        {
            ExitCode = SetupExitCodes.Ok; // user-initiated cancel is not an error
            Close();
        }
    }

    private void OnBrowse()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose the PAX Cookbook install folder" };
        try { if (Directory.Exists(_txtPath.Text)) dlg.SelectedPath = _txtPath.Text; } catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            // Keep the product folder name on the chosen path.
            var chosen = dlg.SelectedPath;
            if (!string.Equals(Path.GetFileName(chosen.TrimEnd(Path.DirectorySeparatorChar)),
                    ProductConstants.InstallRootFolderName, StringComparison.OrdinalIgnoreCase))
                chosen = Path.Combine(chosen, ProductConstants.InstallRootFolderName);
            _txtPath.Text = chosen;
            UpdateFreeSpace();
        }
    }

    private void UpdateFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_txtPath.Text));
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                double freeGb = di.AvailableFreeSpace / 1024d / 1024d / 1024d;
                _freeSpaceLabel.Text = $"Approximate space required: 250 MB.  Drive {di.Name.TrimEnd('\\')} has {freeGb:0.0} GB free.";
                return;
            }
        }
        catch { /* fall through */ }
        _freeSpaceLabel.Text = "Approximate space required: 250 MB.";
    }

    // -----------------------------------------------------------------
    // Prerequisite detection (display only in this slice)
    // -----------------------------------------------------------------
    private void StartDetection()
    {
        _prereqHeading.Text = "Checking prerequisites…";
        _dotnet8Line.Text = "• .NET 8 Desktop Runtime: checking…";
        _ps7Line.Text = "• PowerShell 7: checking…";
        _pyLine.Text = "• Python: checking…";
        _prereqIntro.Text = "";
        _chkInstallPs7.Visible = false;
        _chkInstallPy.Visible = false;
        _btnNext.Enabled = false;

        Task.Run(() =>
        {
            var dotnet8 = _detector.DetectDotNet8DesktopRuntime();
            var ps7 = _detector.DetectPowerShell7();
            var py = _detector.DetectPython();
            BeginInvokeSafe(() => RenderPrereq(dotnet8, ps7, py));
        });
    }

    private void RenderPrereq(PrerequisiteStatus dotnet8, PrerequisiteStatus ps7, PrerequisiteStatus py)
    {
        _dotnet8Status = dotnet8;
        _ps7Status = ps7;
        _pyStatus = py;
        _dotnet8Line.Text = (dotnet8.Satisfied ? "✓  " : "⚠  ") + dotnet8.ToDisplayLine();
        _ps7Line.Text = (ps7.Satisfied ? "✓  " : "⚠  ") + ps7.ToDisplayLine();
        _pyLine.Text = (py.Satisfied ? "✓  " : "⚠  ") + py.ToDisplayLine();
        _dotnet8Line.ForeColor = dotnet8.Satisfied ? Color.FromArgb(0x1B, 0x7F, 0x2E) : Color.FromArgb(0xC8, 0x1F, 0x1F);
        _ps7Line.ForeColor = ps7.Satisfied ? Color.FromArgb(0x1B, 0x7F, 0x2E) : Color.FromArgb(0x9A, 0x6A, 0x00);
        _pyLine.ForeColor = py.Satisfied ? Color.FromArgb(0x1B, 0x7F, 0x2E) : Color.FromArgb(0x9A, 0x6A, 0x00);

        bool dotnet8Met = dotnet8.Satisfied;
        bool optionalsMet = ps7.Satisfied && py.Satisfied;
        
        if (!dotnet8Met)
        {
            _prereqHeading.Text = "Required prerequisite missing";
            _prereqIntro.Text = ".NET 8 Desktop Runtime is required to run PAX Cookbook. Click Install to download and install it automatically.";
            _chkInstallPs7.Visible = false;
            _chkInstallPy.Visible = false;
            _btnNext.Enabled = false;
        }
        else if (optionalsMet)
        {
            _prereqHeading.Text = "All prerequisites met";
            _prereqIntro.Text = "All prerequisites are present. Click Next to continue.";
            _chkInstallPs7.Visible = false;
            _chkInstallPy.Visible = false;
            _btnNext.Enabled = true;
        }
        else
        {
            _prereqHeading.Text = "Prerequisites";
            _prereqIntro.Text = "The following will be installed automatically:";
            _chkInstallPs7.Visible = !ps7.Satisfied;
            _chkInstallPy.Visible = !py.Satisfied;
            _btnNext.Enabled = true;
        }
    }

    // -----------------------------------------------------------------
    // Install
    // -----------------------------------------------------------------
    private void BeginInstall()
    {
        var chosen = _txtPath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(chosen))
        {
            MessageBox.Show("Please choose an install folder.", "PAX Cookbook Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { _installRoot = Path.GetFullPath(chosen); }
        catch
        {
            MessageBox.Show("The install folder path is not valid.", "PAX Cookbook Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _installRunning = true;
        _log.Write("wizard-install-begin", fields: new Dictionary<string, object?>
        {
            ["installRoot"] = _installRoot,
            ["dotnet8"] = _dotnet8Status?.DetectionSource ?? "unknown",
            ["ps7"] = _ps7Status?.DetectionSource ?? "unknown",
            ["python"] = _pyStatus?.DetectionSource ?? "unknown"
        });
        ShowStep(Step.Progress);
        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressLog.Clear();
        AppendLog("Installing PAX Cookbook to:");
        AppendLog("  " + _installRoot);

        // .NET 8 is always required. PowerShell 7 and Python are optional (checkboxes).
        bool wantDotNet8 = _dotnet8Status is { Satisfied: false };
        bool wantPs7 = _ps7Status is { Satisfied: false } && _chkInstallPs7.Checked;
        bool wantPy = _pyStatus is { Satisfied: false } && _chkInstallPy.Checked;

        Task.Run(() =>
        {
            // Fresh, randomly-named per-user download folder, deleted wholesale
            // when done (TOCTOU hardening: a download dir not shared/predictable).
            string tempDir = Path.Combine(Path.GetTempPath(),
                "PAXSetup_" + Guid.NewGuid().ToString("N"));
            var prereq = Array.Empty<NamedPrerequisiteResult>() as IReadOnlyList<NamedPrerequisiteResult>;
            try
            {
                Action<string> progress = msg =>
                    BeginInvokeSafe(() => { _progressStatus.Text = msg; AppendLog(msg); });

                if (wantDotNet8 || wantPs7 || wantPy)
                {
                    if (wantDotNet8)
                        BeginInvokeSafe(() => AppendLog(
                            "Installing prerequisites…"));

                    using var downloader = new HttpPrereqDownloader();
                    var coordinator = new PrerequisiteCoordinator(new IPrerequisiteInstaller[]
                    {
                        new DotNet8DesktopRuntimeInstaller(downloader, new RealElevatedLauncher(), _detector),
                        new PowerShell7Installer(downloader, new RealElevatedLauncher(), _detector),
                        new PythonInstaller(downloader, new RealSilentLauncher(), _detector)
                    });
                    var wanted = new Dictionary<PrerequisiteKind, bool>
                    {
                        [PrerequisiteKind.DotNet8DesktopRuntime] = wantDotNet8,
                        [PrerequisiteKind.PowerShell7] = wantPs7,
                        [PrerequisiteKind.Python] = wantPy
                    };
                    prereq = coordinator.Run(wanted, tempDir, progress, OnPrereqError);
                    foreach (var r in prereq)
                        BeginInvokeSafe(() => AppendLog($"  {r.DisplayName}: {Describe(r.Result.Outcome)}"));
                }

                var result = WizardInstallRunner.Run(
                    _installRoot, payloadRootOverride: null,
                    progress: progress, log: _log, shellOps: _shellOps);
                BeginInvokeSafe(() => OnInstallFinished(result, prereq));
            }
            catch (Exception ex)
            {
                // Defence in depth: the installers + runner are designed never
                // to throw, but if a contract is ever violated we must still
                // reach OnInstallFinished — otherwise _installRunning stays true
                // and the window can never close. Degrade to a graceful failure.
                _log.Write("wizard-install-unhandled", "error",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
                BeginInvokeSafe(() => OnInstallFinished(
                    new WizardInstallResult(false, SetupExitCodes.GenericError,
                        "Unexpected error during installation: " + ex.Message),
                    prereq));
            }
            finally
            {
                TryDeleteDir(tempDir);
            }
        });
    }

    // Invoked on a background thread by the coordinator when a prerequisite
    // install fails or is declined. Prompts (synchronously, on the UI thread)
    // for Retry or Skip.
    private RetrySkipDecision OnPrereqError(PrerequisiteKind kind, string message)
    {
        var name = kind == PrerequisiteKind.PowerShell7 ? "PowerShell 7" : "Python";
        return InvokeSafeSync(
            () => RetrySkipDialog.Show(this, "PAX Cookbook Setup",
                $"{name} could not be installed:\n\n{message}\n\n" +
                "Click Retry to try again, or Skip to continue installing PAX Cookbook without it."),
            RetrySkipDecision.Skip);
    }

    private static string Describe(PrerequisiteInstallOutcome o) => o switch
    {
        PrerequisiteInstallOutcome.Installed => "installed",
        PrerequisiteInstallOutcome.AlreadyPresent => "already present",
        PrerequisiteInstallOutcome.UserDeclined => "not installed (declined)",
        PrerequisiteInstallOutcome.Skipped => "skipped",
        _ => "not installed"
    };

    private void OnInstallFinished(WizardInstallResult result, IReadOnlyList<NamedPrerequisiteResult> prereq)
    {
        _installRunning = false;
        _prereqResults = prereq;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = 100;

        if (result.Success)
        {
            _prereqWarning.Text = BuildPrereqWarning();
            ShowStep(Step.Complete);
            return;
        }

        ExitCode = result.ExitCode;
        _installFailed = true;
        _progressStatus.Text = "Installation failed.";
        _progressStatus.ForeColor = Color.FromArgb(0xB0, 0x20, 0x20);
        AppendLog("");
        AppendLog("ERROR: " + (result.Error ?? "Unknown error."));
        _btnCancel.Text = "Close";
        _btnCancel.Visible = true;
    }

    // A non-blocking warning for the Complete screen when a prerequisite the
    // user needs is still missing (the prompt's "show a warning, don't block").
    private string BuildPrereqWarning()
    {
        var warns = new List<string>();
        if (_dotnet8Status is { Satisfied: false } && !PrereqEndedSatisfied(PrerequisiteKind.DotNet8DesktopRuntime))
            warns.Add("⚠ .NET 8 Desktop Runtime is not installed. Try installing it manually from https://dotnet.microsoft.com/download/dotnet/8.0");
        if (_ps7Status is { Satisfied: false } && !PrereqEndedSatisfied(PrerequisiteKind.PowerShell7))
            warns.Add("⚠ PowerShell 7 is not installed. Bakes need it — install it later from https://aka.ms/powershell.");
        if (_pyStatus is { Satisfied: false } && !PrereqEndedSatisfied(PrerequisiteKind.Python))
            warns.Add("⚠ Python is not installed. Install it later from https://www.python.org/downloads/ if a recipe needs it.");
        return string.Join(Environment.NewLine, warns);
    }

    private bool PrereqEndedSatisfied(PrerequisiteKind kind)
    {
        foreach (var r in _prereqResults)
            if (r.Kind == kind) return r.Result.Satisfied;
        return false;
    }

    private void FinishAndClose()
    {
        // Honour the "Start at login" choice (the install already registered
        // auto-start; uncheck removes it). Then optionally launch the app.
        try { AutoStartChoice.Apply(_installRoot, _chkAutoStart.Checked); } catch { }

        if (_chkLaunch.Checked)
            TryLaunchApp();

        ExitCode = SetupExitCodes.Ok;
        Close();
    }

    private void TryLaunchApp()
    {
        try
        {
            var appExe = ShortcutCatalog.AppExePath(_installRoot);
            if (!File.Exists(appExe)) return;
            var ws = Path.Combine(_installRoot, ProductConstants.WorkspaceFolderName);
            var ar = Path.Combine(_installRoot, ProductConstants.AppRootFolderName);
            var psi = new ProcessStartInfo { FileName = appExe, UseShellExecute = true };
            psi.ArgumentList.Add("--workspace"); psi.ArgumentList.Add(ws);
            psi.ArgumentList.Add("--approot"); psi.ArgumentList.Add(ar);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.Write("wizard-launch-failed", "warn",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------
    private void AppendLog(string line)
    {
        _progressLog.AppendText(line + Environment.NewLine);
    }

    private void BeginInvokeSafe(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
        }
        catch { /* form closing */ }
    }

    // Synchronous marshal to the UI thread for a prompt whose answer the
    // background install thread must wait for (Retry/Skip). Falls back to a
    // safe default if the form is gone.
    private T InvokeSafeSync<T>(Func<T> func, T fallback)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                return (T)Invoke(func);
        }
        catch { /* form closing */ }
        return fallback;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Never allow the window to close while files are being written.
        if (_installRunning)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    private static string DisplayVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null || (v.Major == 0 && v.Minor == 0 && v.Build == 0)) return "1.0.0";
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
