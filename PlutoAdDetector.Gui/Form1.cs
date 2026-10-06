using System.Diagnostics;
using System.Text.Json;

namespace PlutoAdDetector.Gui;

public partial class Form1 : Form
{
    private Process? _detectorProcess;

    private static readonly string SettingsDirectory =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlutoAdDetector");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDirectory, "settings.json");

    private sealed record GuiSettings(string? ChannelUrl);

    private readonly TextBox _sourceUrl = new()
    {
        Dock = DockStyle.Fill,
        Text = "https://pluto.tv/live-tv"
    };

    private readonly RadioButton _plutoMode = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "Pluto (recommended)"
    };

    private readonly RadioButton _compatibilityMode = new()
    {
        AutoSize = true,
        Text = "Try Another Service / Pluto Broke (uses more CPU)"
    };

    private readonly RadioButton _channelMode = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "YouTube channel"
    };

    private readonly RadioButton _singleVideoMode = new()
    {
        AutoSize = true,
        Text = "Single YouTube video"
    };

    private readonly TextBox _channelUrl = new()
    {
        Dock = DockStyle.Fill,
        Text = "https://www.youtube.com/@MeidasTouch"
    };

    private readonly TextBox _singleVideoUrl = new()
    {
        Dock = DockStyle.Fill,
        PlaceholderText = "https://www.youtube.com/watch?v=..."
    };

    private readonly NumericUpDown _minimumDuration = new()
    {
        Minimum = 1,
        Maximum = 86400,
        Value = 300,
        Width = 100
    };

    private readonly CheckBox _visualDetection = new()
    {
        AutoSize = true,
        Checked = true,
        Text = "Use visual ad detection"
    };

    private readonly CheckBox _resume = new()
    {
        AutoSize = true,
        Text = "Resume previous YouTube queue"
    };

    private readonly Button _startButton = new()
    {
        AutoSize = true,
        Text = "Start"
    };

    private readonly Button _stopButton = new()
    {
        AutoSize = true,
        Enabled = false,
        Text = "Stop"
    };

    private readonly Label _status = new()
    {
        AutoSize = true,
        Text = "Stopped"
    };

    private readonly TextBox _output = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9F),
        MinimumSize = new Size(0, 220)
    };

    public Form1()
    {
        InitializeComponent();

        Text = "PlutoAdDetector";
        MinimumSize = new Size(760, 700);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        LoadSettings();

        _channelMode.CheckedChanged += (_, _) => UpdateYoutubeMode();
        _singleVideoMode.CheckedChanged += (_, _) => UpdateYoutubeMode();

        _startButton.Click += (_, _) => StartDetector();
        _stopButton.Click += (_, _) => StopDetector();

        FormClosing += (_, _) => StopDetector();

        _helpButton.Click += (_, _) => ShowHelp();

        UpdateYoutubeMode();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 8
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var i = 0; i < 7; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(CreateLabeledPanel("Source / streaming service URL", _sourceUrl));

        var scanGroup = new GroupBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Text = "Detection mode"
        };

        var scanPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(8)
        };

        scanPanel.Controls.Add(_plutoMode);
        scanPanel.Controls.Add(_compatibilityMode);
        scanGroup.Controls.Add(scanPanel);

        root.Controls.Add(scanGroup);

        var youtubeGroup = new GroupBox
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Text = "YouTube replacement"
        };

        var youtubeLayout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 2,
            RowCount = 4
        };

        youtubeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        youtubeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        youtubeLayout.Controls.Add(_channelMode, 0, 0);
        youtubeLayout.SetColumnSpan(_channelMode, 2);

        youtubeLayout.Controls.Add(new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = "Channel URL:"
        }, 0, 1);
        youtubeLayout.Controls.Add(_channelUrl, 1, 1);

        youtubeLayout.Controls.Add(_singleVideoMode, 0, 2);
        youtubeLayout.SetColumnSpan(_singleVideoMode, 2);

        youtubeLayout.Controls.Add(new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Text = "Video URL:"
        }, 0, 3);
        youtubeLayout.Controls.Add(_singleVideoUrl, 1, 3);

        youtubeGroup.Controls.Add(youtubeLayout);
        root.Controls.Add(youtubeGroup);

        var durationPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top
        };

        durationPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 6, 0),
            Text = "Minimum channel video length (seconds):"
        });
        durationPanel.Controls.Add(_minimumDuration);

        root.Controls.Add(durationPanel);

        var optionsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top
        };

        optionsPanel.Controls.Add(_visualDetection);
        optionsPanel.Controls.Add(_resume);

        root.Controls.Add(optionsPanel);

        var buttonsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top
        };

        buttonsPanel.Controls.Add(_startButton);
        buttonsPanel.Controls.Add(_stopButton);
        buttonsPanel.Controls.Add(_helpButton);
        buttonsPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
            Text = "Status:"
        });
        buttonsPanel.Controls.Add(_status);

        root.Controls.Add(buttonsPanel);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Detector output"
        });

        root.Controls.Add(_output);

        Controls.Add(root);
    }

    private static Control CreateLabeledPanel(string label, Control control)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = label
        });

        panel.Controls.Add(control);

        return panel;
    }

    private void UpdateYoutubeMode()
    {
        _channelUrl.Enabled = _channelMode.Checked;
        _minimumDuration.Enabled = _channelMode.Checked;
        _resume.Enabled = _channelMode.Checked;

        _singleVideoUrl.Enabled = _singleVideoMode.Checked;

        if (_singleVideoMode.Checked)
            _resume.Checked = false;
    }

    private void StartDetector()
    {
        if (_detectorProcess is { HasExited: false })
            return;

        var detector = FindDetector();

        if (detector is null)
        {
            MessageBox.Show(
                this,
                "Could not find PlutoAdDetector.exe.\n\nBuild the complete solution first.",
                "PlutoAdDetector",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return;
        }

        if (string.IsNullOrWhiteSpace(_sourceUrl.Text))
        {
            MessageBox.Show(this, "Enter a streaming service URL.");
            return;
        }

        if (_channelMode.Checked && string.IsNullOrWhiteSpace(_channelUrl.Text))
        {
            MessageBox.Show(this, "Enter a YouTube channel URL.");
            return;
        }

        if (_singleVideoMode.Checked && string.IsNullOrWhiteSpace(_singleVideoUrl.Text))
        {
            MessageBox.Show(this, "Enter a YouTube video URL.");
            return;
        }

        SaveSettings();

        var startInfo = new ProcessStartInfo
        {
            FileName = detector.Value.Path,
            WorkingDirectory = detector.Value.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(_sourceUrl.Text.Trim());

        startInfo.ArgumentList.Add("--scan-mode");
        startInfo.ArgumentList.Add(_plutoMode.Checked ? "focused" : "full");

        if (_channelMode.Checked)
        {
            startInfo.ArgumentList.Add("--channel-url");
            startInfo.ArgumentList.Add(_channelUrl.Text.Trim());

            startInfo.ArgumentList.Add("--min-duration-seconds");
            startInfo.ArgumentList.Add(((int)_minimumDuration.Value).ToString());

            if (_resume.Checked)
                startInfo.ArgumentList.Add("--resume");
        }
        else
        {
            startInfo.ArgumentList.Add("--youtube-url");
            startInfo.ArgumentList.Add(_singleVideoUrl.Text.Trim());
        }

        if (!_visualDetection.Checked)
            startInfo.ArgumentList.Add("--no-visual");

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                AppendOutput(eventArgs.Data);
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                AppendOutput(eventArgs.Data);
        };

        process.Exited += (_, _) =>
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    if (_detectorProcess != process)
                        return;

                    int? exitCode = null;

                    try
                    {
                        exitCode = process.ExitCode;
                    }
                    catch
                    {
                        // Process may already be unavailable during shutdown.
                    }

                    _detectorProcess = null;

                    _startButton.Enabled = true;
                    _stopButton.Enabled = false;
                    _status.Text = exitCode is null
                        ? "Stopped"
                        : $"Stopped (exit code {exitCode})";

                    process.Dispose();
                });
            }
        };

        try
        {
            _output.Clear();

            AppendOutput($"Starting {detector.Value.Path}");
            AppendOutput($"Working directory: {detector.Value.WorkingDirectory}");
            AppendOutput("");

            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");

            _detectorProcess = process;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _startButton.Enabled = false;
            _stopButton.Enabled = true;
            _status.Text = "Running";
        }
        catch (Exception exception)
        {
            process.Dispose();

            MessageBox.Show(
                this,
                $"Could not start PlutoAdDetector:\n\n{exception.Message}",
                "PlutoAdDetector",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void StopDetector()
    {
        var process = _detectorProcess;

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                _status.Text = "Stopping...";
                _stopButton.Enabled = false;

                // The detector launches Chromium children, so terminate the
                // complete tree rather than leaving Chrome processes behind.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and Kill().
        }
        catch (Exception exception)
        {
            AppendOutput($"Could not stop detector: {exception.Message}");
        }
    }

    private void AppendOutput(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendOutput(message));
            return;
        }

        _output.AppendText(message + Environment.NewLine);
        _output.SelectionStart = _output.TextLength;
        _output.ScrollToCaret();
    }

    private static (string Path, string WorkingDirectory)? FindDetector()
    {
        // Published/distributed layout: detector sits beside the GUI.
        var besideGui = Path.Combine(
            AppContext.BaseDirectory,
            "PlutoAdDetector.exe");

        if (File.Exists(besideGui))
            return (besideGui, AppContext.BaseDirectory);

        // Development layout:
        //
        // PlutoAdDetector/
        //   bin/Debug/net10.0/PlutoAdDetector.exe
        //   PlutoAdDetector.Gui/bin/Debug/net10.0-windows/...
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                ".."));

        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var developmentBuild = Path.Combine(
                repositoryRoot,
                "bin",
                configuration,
                "net10.0",
                "PlutoAdDetector.exe");

            if (File.Exists(developmentBuild))
                return (developmentBuild, repositoryRoot);
        }

        return null;
    }

    private readonly Button _helpButton = new()
    {
        AutoSize = true,
        Text = "Help / Keys"
    };

    private void ShowHelp()
    {
        const string help = """
        PlutoAdDetector Keyboard Shortcuts

        These shortcuts work in the browser window while PlutoAdDetector is running.

        T    Train a visual signature from the current source
        V    Toggle learned visual ad detection
        P    Pause / resume ad tracking
        X    Reset the detector and restore the source

        YouTube player:

        N    Skip the current YouTube video
        R    Reload / restore the YouTube queue
        H    Show YouTube player help
        ?    Show YouTube player help

        Detection Modes

        Pluto
        Optimized for Pluto TV. Uses the focused detector.

        Try Another Service / Pluto Broke
        Scans more of the visible page. This may work with other streaming
        services or if Pluto changes its page, but uses more CPU.

        Visual Training

        Press T while the streaming page is showing an ad you want the
        detector to learn. Training progress and results appear in the
        detector output.
        """;

        MessageBox.Show(
            this,
            help,
            "PlutoAdDetector Help",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<GuiSettings>(json);

            if (!string.IsNullOrWhiteSpace(settings?.ChannelUrl))
                _channelUrl.Text = settings.ChannelUrl;
        }
        catch (Exception exception)
        {
            AppendOutput($"Could not load GUI settings: {exception.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var settings = new GuiSettings(
                string.IsNullOrWhiteSpace(_channelUrl.Text)
                    ? null
                    : _channelUrl.Text.Trim());

            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception exception)
        {
            AppendOutput($"Could not save GUI settings: {exception.Message}");
        }
    }
}