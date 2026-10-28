using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TranssionLab.Setup;

internal sealed class SetupWizardForm : Form
{
    private readonly TextBox _pathBox;
    private readonly Button _browseButton;
    private readonly CheckBox _desktopShortcutCheck;
    private readonly CheckBox _startMenuShortcutCheck;
    private readonly CheckBox _launchOnFinishCheck;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Button _installButton;
    private readonly Button _cancelButton;

    private bool _isInstalling;
    private bool _isCompleted;
    private string _completedInstallDir = string.Empty;

    public SetupWizardForm()
    {
        Text = $"{SetupEngine.AppFullName} v{SetupEngine.AppVersion} — First-Time Setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(640, 445);
        BackColor = Color.FromArgb(11, 17, 32);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        AutoScaleMode = AutoScaleMode.Dpi;

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                Icon = Icon.ExtractAssociatedIcon(exePath!);
            }
        }
        catch
        {
        }

        // Top Banner Panel
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Color.FromArgb(19, 28, 49),
            Padding = new Padding(22, 16, 22, 14)
        };

        var titleLabel = new Label
        {
            Text = $"TranssionLab Pro v{SetupEngine.AppVersion} — First-Time Setup",
            Font = new Font("Segoe UI Semibold", 14.5f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(22, 16)
        };

        var subtitleLabel = new Label
        {
            Text = "Offline-First Windows 10/11 Servicing Suite • TECNO • Infinix • itel • Bundled ADB, Fastboot & VCOM Engine",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(22, 50)
        };

        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subtitleLabel);
        Controls.Add(headerPanel);

        // Summary Card
        var infoLabel = new Label
        {
            Text = "• Unpacks the self-contained .NET 8 + WPF desktop suite (zero external runtime or Admin rights needed).\r\n" +
                   "• Bundles official Apache-2.0 Android Platform-Tools (adb.exe & fastboot.exe) for 100% offline use.\r\n" +
                   "• Configures 125 Hz MediaTek & SPD/Unisoc VCOM Catch-and-Hold + 8 reference device profiles.",
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(24, 108),
            Size = new Size(592, 66)
        };
        Controls.Add(infoLabel);

        // Installation Folder Picker
        var folderHeader = new Label
        {
            Text = "Installation Directory (Per-User Standard Path):",
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(226, 232, 240),
            AutoSize = true,
            Location = new Point(24, 184)
        };
        Controls.Add(folderHeader);

        _pathBox = new TextBox
        {
            Text = SetupEngine.GetDefaultInstallDirectory(),
            Location = new Point(24, 210),
            Size = new Size(492, 27),
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_pathBox);

        _browseButton = new Button
        {
            Text = "Browse...",
            Location = new Point(524, 208),
            Size = new Size(92, 29),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        _browseButton.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _browseButton.Click += OnBrowseClick;
        Controls.Add(_browseButton);

        // Options Checkboxes
        _desktopShortcutCheck = new CheckBox
        {
            Text = "Create Desktop shortcut (TranssionLab)",
            Checked = true,
            AutoSize = true,
            Location = new Point(24, 250),
            ForeColor = Color.FromArgb(226, 232, 240)
        };
        Controls.Add(_desktopShortcutCheck);

        _startMenuShortcutCheck = new CheckBox
        {
            Text = "Create Windows Start Menu shortcuts",
            Checked = true,
            AutoSize = true,
            Location = new Point(24, 278),
            ForeColor = Color.FromArgb(226, 232, 240)
        };
        Controls.Add(_startMenuShortcutCheck);

        _launchOnFinishCheck = new CheckBox
        {
            Text = "Launch TranssionLab immediately when setup finishes",
            Checked = true,
            AutoSize = true,
            Location = new Point(24, 306),
            ForeColor = Color.FromArgb(167, 243, 208)
        };
        Controls.Add(_launchOnFinishCheck);

        // Progress & Status
        _statusLabel = new Label
        {
            Text = "Ready to install. Click 'Install Now' to begin.",
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoEllipsis = true,
            Location = new Point(24, 344),
            Size = new Size(592, 20)
        };
        Controls.Add(_statusLabel);

        _progressBar = new ProgressBar
        {
            Location = new Point(24, 368),
            Size = new Size(592, 16),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous
        };
        Controls.Add(_progressBar);

        // Bottom Buttons
        _installButton = new Button
        {
            Text = "Install Now",
            Location = new Point(476, 398),
            Size = new Size(140, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _installButton.FlatAppearance.BorderSize = 0;
        _installButton.Click += OnInstallOrFinishClick;
        Controls.Add(_installButton);

        _cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(372, 398),
            Size = new Size(94, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(226, 232, 240),
            Cursor = Cursors.Hand
        };
        _cancelButton.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _cancelButton.Click += (_, _) =>
        {
            if (!_isInstalling)
            {
                Close();
            }
        };
        Controls.Add(_cancelButton);

        AcceptButton = _installButton;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isInstalling)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where to install TranssionLab:",
            SelectedPath = _pathBox.Text,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            _pathBox.Text = dialog.SelectedPath;
        }
    }

    private async void OnInstallOrFinishClick(object? sender, EventArgs e)
    {
        if (_isCompleted)
        {
            if (_launchOnFinishCheck.Checked)
            {
                var exe = Path.Combine(_completedInstallDir, SetupEngine.MainExeName);
                if (File.Exists(exe))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            WorkingDirectory = _completedInstallDir,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            this,
                            $"Installed cleanly, but could not auto-launch '{exe}':\n{ex.Message}",
                            SetupEngine.AppName,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }

            Close();
            return;
        }

        var targetDir = _pathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show(this, "Please choose a valid installation directory.", SetupEngine.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isInstalling = true;
        _pathBox.Enabled = false;
        _browseButton.Enabled = false;
        _desktopShortcutCheck.Enabled = false;
        _startMenuShortcutCheck.Enabled = false;
        _installButton.Enabled = false;
        _cancelButton.Enabled = false;
        _installButton.Text = "Installing...";

        var options = new SetupInstallOptions
        {
            InstallDirectory = targetDir,
            CreateDesktopShortcut = _desktopShortcutCheck.Checked,
            CreateStartMenuShortcut = _startMenuShortcutCheck.Checked
        };

        try
        {
            await Task.Run(() =>
            {
                SetupEngine.Install(options, (current, total, msg) =>
                {
                    int pct = total > 0 ? Math.Min(100, Math.Max(0, (int)Math.Round(current * 100.0 / total))) : 0;
                    BeginInvoke(new Action(() =>
                    {
                        _progressBar.Value = pct;
                        _statusLabel.Text = msg;
                    }));
                });
            });

            _isInstalling = false;
            _isCompleted = true;
            _completedInstallDir = Path.GetFullPath(targetDir);

            _progressBar.Value = 100;
            _statusLabel.ForeColor = Color.FromArgb(110, 231, 183);
            _statusLabel.Text = $"✓ Installed successfully to {_completedInstallDir}";
            _installButton.BackColor = Color.FromArgb(5, 150, 105);
            _installButton.Text = _launchOnFinishCheck.Checked ? "Launch & Finish" : "Finish";
            _installButton.Enabled = true;
            _installButton.Focus();
        }
        catch (Exception ex)
        {
            _isInstalling = false;
            _pathBox.Enabled = true;
            _browseButton.Enabled = true;
            _desktopShortcutCheck.Enabled = true;
            _startMenuShortcutCheck.Enabled = true;
            _installButton.Enabled = true;
            _cancelButton.Enabled = true;
            _installButton.Text = "Retry Install";
            _statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
            _statusLabel.Text = $"Setup error: {ex.Message}";

            MessageBox.Show(
                this,
                $"Setup could not complete:\n\n{ex.Message}",
                $"{SetupEngine.AppName} Setup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
