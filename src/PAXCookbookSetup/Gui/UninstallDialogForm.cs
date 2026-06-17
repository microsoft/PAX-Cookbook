using System.Drawing;
using System.Windows.Forms;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Verbs;

namespace PAXCookbookSetup.Gui;

// A minimal two-step uninstall dialog: a confirmation, then a progress view
// that runs the real UninstallVerb and reports staged progress. Used when the
// user uninstalls interactively (Add/Remove Programs or the Start Menu
// shortcut); scripted/silent uninstall (--quiet) keeps the console CLI path.
internal sealed class UninstallForm : Form
{
    private readonly string _installRoot;
    private readonly ParsedArgs _args;
    private readonly SetupLogger _log;

    public int ExitCode { get; private set; } = SetupExitCodes.Ok;

    private Panel _confirmPanel = null!, _progressPanel = null!;
    private Button _btnUninstall = null!, _btnCancel = null!, _btnClose = null!;
    private ProgressBar _progressBar = null!;
    private Label _progressStatus = null!;
    private bool _running;

    public UninstallForm(string installRoot, ParsedArgs args, SetupLogger log)
    {
        _installRoot = installRoot;
        _args = args;
        _log = log;
        BuildForm();
    }

    private void BuildForm()
    {
        Text = "Uninstall PAX Cookbook";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 300);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5F);
        try { Icon = WizardAssets.LoadAppIcon(); } catch { /* default icon */ }

        _confirmPanel = BuildConfirmPanel();
        _progressPanel = BuildProgressPanel();
        foreach (var p in new[] { _confirmPanel, _progressPanel })
        {
            p.Dock = DockStyle.Fill;
            p.Visible = false;
            Controls.Add(p);
        }
        // The branding header is added LAST so it reserves the top edge and the
        // Fill panels take the remainder (same z-order pattern as SetupWizardForm).
        Controls.Add(BuildLogoHeader());
        _confirmPanel.Visible = true;
        _confirmPanel.BringToFront();
    }

    // A top header strip carrying both clickable logos: the PAX Cookbook brand
    // (left) and the Microsoft Open Source logo (right).
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

    private Panel BuildConfirmPanel()
    {
        var p = new Panel { Padding = new Padding(24) };
        var heading = new Label
        {
            Text = "Are you sure you want to uninstall PAX Cookbook?",
            Location = new Point(24, 28), Size = new Size(470, 28),
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold)
        };
        var body = new Label
        {
            Text = "This will remove the application files. Your recipes and output data " +
                   "will be preserved.",
            Location = new Point(24, 66), Size = new Size(470, 56),
            Font = new Font("Segoe UI", 10F)
        };
        _btnUninstall = new Button { Text = "Uninstall", Size = new Size(110, 32), Location = new Point(280, 176) };
        _btnCancel = new Button { Text = "Cancel", Size = new Size(110, 32), Location = new Point(398, 176) };
        _btnUninstall.Click += (_, _) => StartUninstall();
        _btnCancel.Click += (_, _) => { ExitCode = SetupExitCodes.Ok; Close(); };
        p.Controls.Add(heading);
        p.Controls.Add(body);
        p.Controls.Add(_btnUninstall);
        p.Controls.Add(_btnCancel);
        return p;
    }

    private Panel BuildProgressPanel()
    {
        var p = new Panel { Padding = new Padding(24) };
        var heading = new Label
        {
            Text = "Uninstalling PAX Cookbook",
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
            Text = "Starting...",
            Location = new Point(26, 106), Size = new Size(468, 24),
            Font = new Font("Segoe UI", 10F)
        };
        _btnClose = new Button { Text = "Close", Size = new Size(110, 32), Location = new Point(398, 176), Visible = false };
        _btnClose.Click += (_, _) => Close();
        p.Controls.Add(heading);
        p.Controls.Add(_progressBar);
        p.Controls.Add(_progressStatus);
        p.Controls.Add(_btnClose);
        return p;
    }

    private void StartUninstall()
    {
        _running = true;
        _confirmPanel.Visible = false;
        _progressPanel.Visible = true;
        _progressPanel.BringToFront();

        Action<string> progress = msg => BeginInvokeSafe(() => _progressStatus.Text = msg);

        Task.Run(() =>
        {
            int rc;
            try
            {
                rc = UninstallVerb.Run(_installRoot, _args, _log, TextWriter.Null,
                                       operations: null, progress: progress);
            }
            catch (Exception ex)
            {
                _log.Write("uninstall-gui-exception", "error",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
                rc = SetupExitCodes.UninstallFailed;
            }
            BeginInvokeSafe(() => OnFinished(rc));
        });
    }

    private void OnFinished(int rc)
    {
        _running = false;
        ExitCode = rc;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = 100;

        if (rc == SetupExitCodes.Ok)
        {
            _progressStatus.Text = "PAX Cookbook has been removed.";
        }
        else
        {
            _progressStatus.Text = "Uninstall did not complete. See the Setup log for details.";
            _progressStatus.ForeColor = Color.FromArgb(0xB0, 0x20, 0x20);
        }
        _btnClose.Visible = true;
    }

    private void BeginInvokeSafe(Action action)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
        catch { /* form closing */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Do not allow closing while files are being removed.
        if (_running) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }
}
