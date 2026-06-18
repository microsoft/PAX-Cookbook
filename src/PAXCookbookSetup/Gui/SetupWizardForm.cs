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
    private Label _prereqHeading = null!, _dotnet8Line = null!, _aspnetLine = null!, _ps7Line = null!, _pyLine = null!, _prereqIntro = null!, _prereqNote = null!;
    private PrerequisiteStatus? _dotnet8Status, _aspnetStatus, _ps7Status, _pyStatus;

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
        ClientSize = new Size(680, 560);
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

    // Appends one styled run to a RichTextBox, preserving the caret at the end
    // so successive calls build a single formatted paragraph block.
    private static void AppendRun(RichTextBox rtb, string text, float size, bool bold, Color color)
    {
        int start = rtb.TextLength;
        rtb.AppendText(text);
        rtb.Select(start, text.Length);
        rtb.SelectionFont = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        rtb.SelectionColor = color;
        rtb.Select(rtb.TextLength, 0);
    }

    private Panel BuildWelcomePanel()
    {
        var p = new Panel { Padding = new Padding(28, 22, 28, 16) };
        var navy = Color.FromArgb(0x0A, 0x1F, 0x44);
        var body = Color.FromArgb(0x20, 0x20, 0x20);
        var muted = Color.FromArgb(0x60, 0x60, 0x60);

        p.Controls.Add(Body("Welcome to PAX Cookbook Setup", 28, 22, 624, 34, 17F, FontStyle.Bold));

        var rtb = new RichTextBox
        {
            Location = new Point(28, 64),
            Size = new Size(624, 316),
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            BackColor = Color.White,
            ScrollBars = RichTextBoxScrollBars.None,
            WordWrap = true,
            TabStop = false,
            Cursor = Cursors.Default
        };
        _ = rtb.Handle; // force handle creation so SelectionFont/Color apply

        AppendRun(rtb,
            "PAX Cookbook automates pulling Microsoft 365 Copilot adoption and usage data " +
            "from Microsoft Purview audit logs and prepares it for Power BI dashboards.\n\n",
            10.5F, false, body);

        AppendRun(rtb, "\u2022  What it does\u2003", 10.5F, true, navy);
        AppendRun(rtb,
            "Connects to your Microsoft 365 tenant, pulls Copilot usage and adoption data " +
            "from Purview, and outputs it ready for Power BI dashboards.\n",
            10.5F, false, body);

        AppendRun(rtb, "\u2022  Who it\u2019s for\u2003", 10.5F, true, navy);
        AppendRun(rtb,
            "Microsoft 365 admins, IT teams, and analysts measuring Copilot ROI and adoption " +
            "across the organization.\n",
            10.5F, false, body);

        AppendRun(rtb, "\u2022  How it works\u2003", 10.5F, true, navy);
        AppendRun(rtb,
            "A guided recipe builder sets up what data to pull, how to sign in, where to save " +
            "the output, and whether to run on a schedule \u2014 no scripting required.\n",
            10.5F, false, body);

        AppendRun(rtb, "\u2022  What it powers\u2003", 10.5F, true, navy);
        AppendRun(rtb,
            "Ready-made dashboards for AI-in-One, AI Business Value, M365 Usage Analytics, and " +
            "Entra Directory Export \u2014 or build your own recipe.\n",
            10.5F, false, body);

        AppendRun(rtb, "\u2022  Runs locally\u2003", 10.5F, true, navy);
        AppendRun(rtb,
            "Installs on your PC with a system tray icon for background operation and scheduled " +
            "data pulls.",
            10.5F, false, body);

        rtb.Select(0, 0); // keep the view scrolled to the top
        p.Controls.Add(rtb);

        var footer = Body($"Version v{DisplayVersion()}    \u00B7    Click Next to continue.", 28, 388, 624, 18, 9F);
        footer.ForeColor = muted;
        p.Controls.Add(footer);
        return p;
    }

    private Panel BuildPrereqPanel()
    {
        var p = new Panel { Padding = new Padding(28, 24, 28, 16) };
        _prereqHeading = Body("Checking prerequisites…", 28, 24, 612, 28, 11F, FontStyle.Bold);
        _dotnet8Line = Body("• .NET 8 Desktop Runtime: checking…", 40, 64, 600, 24);
        _aspnetLine = Body("• ASP.NET Core 8 Runtime: checking…", 40, 92, 600, 24);
        _ps7Line = Body("• PowerShell 7: checking…", 40, 120, 600, 24);
        _pyLine = Body("• Python: checking…", 40, 148, 600, 24);
        _prereqIntro = Body("", 28, 186, 612, 48, 10F);
        _prereqNote = Body(
            "Note: installing the .NET runtimes and PowerShell 7 may require administrator " +
            "approval. PAX Cookbook itself installs for your user account only and does not " +
            "require administrator rights.",
            28, 240, 612, 60, 9F);
        _prereqNote.ForeColor = Color.FromArgb(0x60, 0x60, 0x60);
        p.Controls.Add(_prereqHeading);
        p.Controls.Add(_dotnet8Line);
        p.Controls.Add(_aspnetLine);
        p.Controls.Add(_ps7Line);
        p.Controls.Add(_pyLine);
        p.Controls.Add(_prereqIntro);
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
        _aspnetLine.Text = "• ASP.NET Core 8 Runtime: checking…";
        _ps7Line.Text = "• PowerShell 7: checking…";
        _pyLine.Text = "• Python: checking…";
        _prereqIntro.Text = "";
        _btnNext.Enabled = false;

        Task.Run(() =>
        {
            var dotnet8 = _detector.DetectDotNet8DesktopRuntime();
            var aspnet = _detector.DetectAspNetCoreRuntime();
            var ps7 = _detector.DetectPowerShell7();
            var py = _detector.DetectPython();
            BeginInvokeSafe(() => RenderPrereq(dotnet8, aspnet, ps7, py));
        });
    }

    private void RenderPrereq(PrerequisiteStatus dotnet8, PrerequisiteStatus aspnet,
                              PrerequisiteStatus ps7, PrerequisiteStatus py)
    {
        _dotnet8Status = dotnet8;
        _aspnetStatus = aspnet;
        _ps7Status = ps7;
        _pyStatus = py;
        _dotnet8Line.Text = (dotnet8.Satisfied ? "✓  " : "⚠  ") + dotnet8.ToDisplayLine();
        _aspnetLine.Text = (aspnet.Satisfied ? "✓  " : "⚠  ") + aspnet.ToDisplayLine();
        _ps7Line.Text = (ps7.Satisfied ? "✓  " : "⚠  ") + ps7.ToDisplayLine();
        _pyLine.Text = (py.Satisfied ? "✓  " : "⚠  ") + py.ToDisplayLine();
        _dotnet8Line.ForeColor = dotnet8.Satisfied ? Color.FromArgb(0x1B, 0x7F, 0x2E) : Color.FromArgb(0xC8, 0x1F, 0x1F);
        _aspnetLine.ForeColor = aspnet.Satisfied ? Color.FromArgb(0x1B, 0x7F, 0x2E) : Color.FromArgb(0xC8, 0x1F, 0x1F);
        _ps7Line.ForeColor = ps7.Satisfied ? Color.FromArgb(0x1B, 0x7F, 0x2E) : Color.FromArgb(0xC8, 0x1F, 0x1F);
        _pyLine.ForeColor = py.Satisfied ? Color.FromArgb(0x1B, 0x7F, 0x2E) : Color.FromArgb(0xC8, 0x1F, 0x1F);

        bool allMet = dotnet8.Satisfied && aspnet.Satisfied && ps7.Satisfied && py.Satisfied;

        if (allMet)
        {
            _prereqHeading.Text = "All prerequisites met";
            _prereqIntro.Text = "All required components are present. Click Next to continue.";
        }
        else
        {
            _prereqHeading.Text = "Required prerequisites";
            int missing = (dotnet8.Satisfied ? 0 : 1) + (aspnet.Satisfied ? 0 : 1)
                        + (ps7.Satisfied ? 0 : 1) + (py.Satisfied ? 0 : 1);
            _prereqIntro.Text = $"PAX Cookbook requires all four components.\n{missing} missing component{(missing > 1 ? "s" : "")} will be installed automatically.";
        }
        _btnNext.Enabled = true;
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
            ["aspnetcore"] = _aspnetStatus?.DetectionSource ?? "unknown",
            ["ps7"] = _ps7Status?.DetectionSource ?? "unknown",
            ["python"] = _pyStatus?.DetectionSource ?? "unknown"
        });
        ShowStep(Step.Progress);
        _progressBar.Style = ProgressBarStyle.Marquee;
        _progressLog.Clear();
        AppendLog("Installing PAX Cookbook to:");
        AppendLog("  " + _installRoot);

        // All three prerequisites are REQUIRED. Install any that are missing.
        bool needDotNet8 = _dotnet8Status is { Satisfied: false };
        bool needAspNet = _aspnetStatus is { Satisfied: false };
        bool needPs7 = _ps7Status is { Satisfied: false };
        bool needPy = _pyStatus is { Satisfied: false };

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

                if (needDotNet8 || needAspNet || needPs7 || needPy)
                {
                    BeginInvokeSafe(() => AppendLog("Installing required prerequisites…"));

                    using var downloader = new HttpPrereqDownloader();
                    var coordinator = new PrerequisiteCoordinator(new IPrerequisiteInstaller[]
                    {
                        new DotNet8DesktopRuntimeInstaller(downloader, new RealElevatedLauncher(), _detector),
                        new AspNetCoreRuntimeInstaller(downloader, new RealElevatedLauncher(), _detector),
                        new PowerShell7Installer(downloader, new RealElevatedLauncher(), _detector),
                        new PythonInstaller(downloader, new RealSilentLauncher(), _detector)
                    });
                    var needed = new Dictionary<PrerequisiteKind, bool>
                    {
                        [PrerequisiteKind.DotNet8DesktopRuntime] = needDotNet8,
                        [PrerequisiteKind.AspNetCoreRuntime] = needAspNet,
                        [PrerequisiteKind.PowerShell7] = needPs7,
                        [PrerequisiteKind.Python] = needPy
                    };
                    var coordResult = coordinator.Run(needed, tempDir, progress, OnPrereqError);
                    prereq = coordResult.Results;
                    foreach (var r in prereq)
                        BeginInvokeSafe(() => AppendLog($"  {r.DisplayName}: {Describe(r.Result.Outcome)}"));

                    // If any required prerequisite was cancelled, abort the install.
                    if (coordResult.IsCancelled)
                    {
                        BeginInvokeSafe(() => OnInstallFinished(
                            new WizardInstallResult(false, SetupExitCodes.GenericError,
                                "Setup was cancelled because a required prerequisite could not be installed."),
                            prereq));
                        return;
                    }
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

    // Invoked on a background thread by the coordinator when a required
    // prerequisite install fails or is declined. Prompts (synchronously, on the
    // UI thread) for Retry or Exit Setup.
    private RetryExitDecision OnPrereqError(PrerequisiteKind kind, string message)
    {
        var name = kind switch
        {
            PrerequisiteKind.DotNet8DesktopRuntime => ".NET 8 Desktop Runtime",
            PrerequisiteKind.AspNetCoreRuntime => "ASP.NET Core 8 Runtime",
            PrerequisiteKind.PowerShell7 => "PowerShell 7",
            PrerequisiteKind.Python => "Python",
            _ => kind.ToString()
        };
        return InvokeSafeSync(
            () => RetryExitDialog.Show(this, "PAX Cookbook Setup",
                $"{name} is required for PAX Cookbook and could not be installed:\n\n{message}\n\n" +
                "Click Retry to try again, or Exit Setup to cancel."),
            RetryExitDecision.ExitSetup);
    }

    private static string Describe(PrerequisiteInstallOutcome o) => o switch
    {
        PrerequisiteInstallOutcome.Installed => "installed",
        PrerequisiteInstallOutcome.AlreadyPresent => "already present",
        PrerequisiteInstallOutcome.UserDeclined => "declined (required)",
        PrerequisiteInstallOutcome.Cancelled => "cancelled",
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

    // Safety-net warning if a required prerequisite somehow wasn't installed.
    // (Should never happen since prerequisites are mandatory and we abort on
    // failure, but kept for defensive visibility.)
    private string BuildPrereqWarning()
    {
        var warns = new List<string>();
        if (_dotnet8Status is { Satisfied: false } && !PrereqEndedSatisfied(PrerequisiteKind.DotNet8DesktopRuntime))
            warns.Add("⚠ .NET 8 Desktop Runtime is required but not installed.");
        if (_aspnetStatus is { Satisfied: false } && !PrereqEndedSatisfied(PrerequisiteKind.AspNetCoreRuntime))
            warns.Add("⚠ ASP.NET Core 8 Runtime is required but not installed.");
        if (_ps7Status is { Satisfied: false } && !PrereqEndedSatisfied(PrerequisiteKind.PowerShell7))
            warns.Add("⚠ PowerShell 7 is required but not installed.");
        if (_pyStatus is { Satisfied: false } && !PrereqEndedSatisfied(PrerequisiteKind.Python))
            warns.Add("⚠ Python is required but not installed.");
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
            // WDAC-safe launch: run the Microsoft-signed dotnet.exe host with the
            // app DLL (the unsigned apphost EXE cannot be executed).
            var appDll = DotNetLaunch.AppDllPath(_installRoot);
            if (!File.Exists(appDll)) return;
            var dotnet = DotNetLaunch.DotNetExePath();
            var ws = Path.Combine(_installRoot, ProductConstants.WorkspaceFolderName);
            var ar = Path.Combine(_installRoot, ProductConstants.AppRootFolderName);
            // CreateNoWindow + UseShellExecute=false suppress dotnet.exe's
            // console window (no blank terminal alongside the app).
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(appDll);
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
