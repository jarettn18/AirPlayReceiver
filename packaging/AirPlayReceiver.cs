// Portable AirPlay receiver: extracts the bundled UxPlay + GStreamer runtime and
// shows a window to start/stop uxplay.exe and edit its settings, plus a tray icon.
// Settings are stored in AirPlayReceiver.ini next to the exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class Program
{
    public const string Title = "AirPlay Receiver";

    [STAThread]
    static void Main()
    {
        // One window only: a second launch brings the existing window to the front.
        bool first;
        using (var mutex = new Mutex(true, @"Local\AirPlayReceiverApp", out first))
        using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AirPlayReceiverShow"))
        {
            if (!first) { show.Set(); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            var listener = new Thread(delegate()
            {
                while (true)
                {
                    show.WaitOne();
                    try { form.BeginInvoke(new Action(form.ShowFromTray)); } catch (InvalidOperationException) { }
                }
            }) { IsBackground = true };
            listener.Start();
            Application.Run(form);
        }
    }
}

// --- Settings (AirPlayReceiver.ini) ---

class Settings
{
    public string Name = Environment.MachineName;
    public string VideoSink = "glimagesink";
    public string AudioSink = "wasapisink";
    public bool Fullscreen;
    public string ExtraArgs = "";
    public bool ExitOnWindowClose = true;
    public bool AutoStart = true;

    public static Settings Load(string path)
    {
        var s = new Settings();
        if (!File.Exists(path)) return s;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.StartsWith("#") || line.StartsWith(";") || eq < 1) continue;
            values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        string v;
        if (values.TryGetValue("Name", out v) && v != "") s.Name = v;
        if (values.TryGetValue("VideoSink", out v) && v != "") s.VideoSink = v;
        if (values.TryGetValue("AudioSink", out v) && v != "") s.AudioSink = v;
        if (values.TryGetValue("Fullscreen", out v)) s.Fullscreen = v == "1";
        if (values.TryGetValue("ExitOnWindowClose", out v)) s.ExitOnWindowClose = v != "0";
        if (values.TryGetValue("AutoStart", out v)) s.AutoStart = v != "0";
        if (values.TryGetValue("ExtraArgs", out v)) s.SetExtraArgs(v);
        return s;
    }

    // Pulls -vs/-as/-fs out of free-form arguments into their own settings
    // (this also migrates older ini files that kept them in ExtraArgs).
    public void SetExtraArgs(string args)
    {
        var rest = new List<string>();
        var tokens = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "-vs" && i + 1 < tokens.Length) VideoSink = tokens[++i];
            else if (tokens[i] == "-as" && i + 1 < tokens.Length) AudioSink = tokens[++i];
            else if (tokens[i] == "-fs") Fullscreen = true;
            else rest.Add(tokens[i]);
        }
        ExtraArgs = string.Join(" ", rest);
    }

    public void Save(string path)
    {
        File.WriteAllLines(path, new[] {
            "# AirPlay Receiver settings. Easiest to edit from the app window.",
            "Name=" + Name,
            "VideoSink=" + VideoSink,
            "AudioSink=" + AudioSink,
            "Fullscreen=" + (Fullscreen ? "1" : "0"),
            "ExtraArgs=" + ExtraArgs,
            "ExitOnWindowClose=" + (ExitOnWindowClose ? "1" : "0"),
            "AutoStart=" + (AutoStart ? "1" : "0"),
        });
    }

    public string BuildArgs()
    {
        var args = "-n \"" + Name.Replace("\"", "") + "\" -nh -vs " + VideoSink + " -as " + AudioSink;
        if (Fullscreen) args += " -fs";
        if (ExtraArgs != "") args += " " + ExtraArgs;
        return args;
    }
}

// --- Main window ---

class MainForm : Form
{
    const string FirewallRule = "AirPlay Receiver";
    static readonly Color Gray = Color.FromArgb(140, 140, 140);
    static readonly Color Amber = Color.FromArgb(214, 140, 0);
    static readonly Color Green = Color.FromArgb(30, 150, 70);
    static readonly Color Blue = Color.FromArgb(0, 110, 210);
    static readonly Color Red = Color.FromArgb(200, 40, 40);

    readonly string runtimeDir, uxplayExe, iniPath, logPath;
    readonly bool firstRun;
    Settings settings;

    Process proc;
    StreamWriter log;
    DateTime started;
    bool stopping, sawWindow, runtimeReady, exiting, hintShown;

    // Controls
    Label dot, statusText, statusDetail, firewallText;
    Button startStop, firewallButton, save;
    TextBox nameBox, extraBox;
    ComboBox videoBox, audioBox;
    CheckBox fullscreenBox, exitOnCloseBox, autoStartBox;
    TableLayoutPanel firewallRow;
    NotifyIcon tray;
    ToolStripMenuItem trayStartStop;
    System.Windows.Forms.Timer timer;

    public MainForm()
    {
        runtimeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPlayReceiver", "runtime");
        uxplayExe = Path.Combine(runtimeDir, "bin", "uxplay.exe");
        iniPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "AirPlayReceiver.ini");
        logPath = Path.Combine(Path.GetTempPath(), "airplay-receiver.log");
        firstRun = !File.Exists(iniPath);
        settings = Settings.Load(iniPath);
        if (firstRun) TrySaveSettings();

        BuildUi();
        LoadSettingsIntoUi();

        timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += delegate { OnTick(); };
        timer.Start();
    }

    // ----- Layout -----

    void BuildUi()
    {
        Text = Program.Title;
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

        // Status row: dot, text, Start/Stop
        var statusRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dot = new Label { Text = "●", Font = new Font("Segoe UI", 20f), AutoSize = true, ForeColor = Gray, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 4, 0) };
        var textStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        statusText = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), Margin = new Padding(0) };
        statusDetail = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 0), MaximumSize = new Size(300, 0) };
        textStack.Controls.Add(statusText);
        textStack.Controls.Add(statusDetail);
        startStop = new Button { Text = "Start", AutoSize = true, MinimumSize = new Size(96, 34), Anchor = AnchorStyles.Right };
        startStop.Click += delegate { if (IsRunning) StopReceiver(); else StartReceiver(); };
        statusRow.Controls.Add(dot, 0, 0);
        statusRow.Controls.Add(textStack, 1, 0);
        statusRow.Controls.Add(startStop, 2, 0);
        root.Controls.Add(statusRow);

        // Firewall warning row (hidden unless the rule is missing)
        firewallRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Visible = false, BackColor = Color.FromArgb(255, 244, 214), Padding = new Padding(8, 4, 4, 4), Margin = new Padding(0, 4, 0, 4) };
        firewallRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        firewallRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        firewallText = new Label { Text = "Firewall rule missing: your iPhone may not see this PC.", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(90, 60, 0), MaximumSize = new Size(260, 0) };
        firewallButton = new Button { Text = "Add rule…", AutoSize = true, Anchor = AnchorStyles.Right };
        firewallButton.Click += delegate { AddFirewallRule(); };
        firewallRow.Controls.Add(firewallText, 0, 0);
        firewallRow.Controls.Add(firewallButton, 1, 0);
        root.Controls.Add(firewallRow);

        // Settings
        var group = new GroupBox { Text = "Settings", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10), Margin = new Padding(0, 8, 0, 0) };
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));

        nameBox = new TextBox { Dock = DockStyle.Fill };
        videoBox = new ComboBox { Dock = DockStyle.Fill };
        videoBox.Items.AddRange(new object[] { "glimagesink", "d3d11videosink", "d3d12videosink", "autovideosink" });
        audioBox = new ComboBox { Dock = DockStyle.Fill };
        audioBox.Items.AddRange(new object[] { "wasapisink", "wasapi2sink", "directsoundsink", "autoaudiosink" });
        extraBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(grid, "Receiver name", nameBox);
        AddRow(grid, "Video output", videoBox);
        AddRow(grid, "Audio output", audioBox);
        AddRow(grid, "Extra arguments", extraBox);

        fullscreenBox = new CheckBox { Text = "Start the video window fullscreen", AutoSize = true };
        exitOnCloseBox = new CheckBox { Text = "Stop the receiver when mirroring ends", AutoSize = true };
        autoStartBox = new CheckBox { Text = "Start the receiver when this app opens", AutoSize = true };
        foreach (var cb in new[] { fullscreenBox, exitOnCloseBox, autoStartBox })
        {
            cb.Margin = new Padding(0, 4, 0, 0);
            grid.Controls.Add(cb);
            grid.SetColumnSpan(cb, 2);
        }

        save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(96, 30), Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0), Enabled = false };
        save.Click += delegate { SaveFromUi(); };
        grid.Controls.Add(save);
        grid.SetColumnSpan(save, 2);
        group.Controls.Add(grid);
        root.Controls.Add(group);

        foreach (Control c in new Control[] { nameBox, videoBox, audioBox, extraBox })
            c.TextChanged += delegate { UpdateSaveButton(); };
        foreach (var cb in new[] { fullscreenBox, exitOnCloseBox, autoStartBox })
            cb.CheckedChanged += delegate { UpdateSaveButton(); };
        // A DropDown ComboBox selects all its text whenever it is resized by layout.
        foreach (var combo in new[] { videoBox, audioBox })
        {
            var box = combo;
            box.SizeChanged += delegate { if (IsHandleCreated && !box.Focused) BeginInvoke(new Action(delegate { if (!box.Focused) box.Select(0, 0); })); };
        }

        // Footer links
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        footer.Controls.Add(MakeLink("Open log", delegate { OpenInNotepad(logPath); }));
        footer.Controls.Add(MakeLink("Exit", delegate { ExitApp(); }));
        root.Controls.Add(footer);

        Controls.Add(root);
        AcceptButton = save;

        // Tray
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, delegate { ShowFromTray(); }).Font = new Font(menu.Font, FontStyle.Bold);
        trayStartStop = (ToolStripMenuItem)menu.Items.Add("Start receiver", null, delegate { if (IsRunning) StopReceiver(); else StartReceiver(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { ExitApp(); });
        tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = Program.Title, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { ShowFromTray(); };
    }

    static void AddRow(TableLayoutPanel grid, string label, Control field)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
        field.Margin = new Padding(0, 3, 0, 3);
        grid.Controls.Add(field);
    }

    static LinkLabel MakeLink(string text, EventHandler onClick)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        link.LinkClicked += delegate { onClick(null, EventArgs.Empty); };
        return link;
    }

    // ----- Lifecycle -----

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = startStop;
        ClearComboSelection();
        SetStatus(Amber, "Preparing…", "Unpacking the receiver (first run only).");
        try
        {
            await Task.Run(new Action(EnsureRuntime));
            runtimeReady = true;
        }
        catch (Exception ex)
        {
            SetStatus(Red, "Not available", "Could not unpack the receiver: " + ex.Message);
            return;
        }
        try { tray.Icon = Icon.ExtractAssociatedIcon(uxplayExe); } catch { }

        bool hasRule = await Task.Run(new Func<bool>(FirewallRuleExists));
        firewallRow.Visible = !hasRule;
        if (!hasRule && firstRun) AddFirewallRule();

        UpdateStatus();
        if (settings.AutoStart && !IsRunning) StartReceiver();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps a running receiver in the tray.
        if (!exiting && e.CloseReason == CloseReason.UserClosing && IsRunning)
        {
            e.Cancel = true;
            Hide();
            if (!hintShown)
            {
                hintShown = true;
                tray.ShowBalloonTip(3000, Program.Title, "Still running. Double-click the tray icon to open it, or right-click to stop.", ToolTipIcon.Info);
            }
            return;
        }
        if (IsRunning) StopReceiver();
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        exiting = true;
        Close();
    }

    // ----- Settings -----

    void LoadSettingsIntoUi()
    {
        nameBox.Text = settings.Name;
        videoBox.Text = settings.VideoSink;
        audioBox.Text = settings.AudioSink;
        extraBox.Text = settings.ExtraArgs;
        fullscreenBox.Checked = settings.Fullscreen;
        exitOnCloseBox.Checked = settings.ExitOnWindowClose;
        autoStartBox.Checked = settings.AutoStart;
        ClearComboSelection();
        UpdateSaveButton();
    }

    // Setting a ComboBox's text selects all of it, which looks like a highlight.
    void ClearComboSelection()
    {
        videoBox.Select(0, 0);
        audioBox.Select(0, 0);
    }

    Settings ReadUi()
    {
        var s = new Settings
        {
            Name = nameBox.Text.Trim() == "" ? Environment.MachineName : nameBox.Text.Trim(),
            VideoSink = videoBox.Text.Trim() == "" ? "glimagesink" : videoBox.Text.Trim(),
            AudioSink = audioBox.Text.Trim() == "" ? "wasapisink" : audioBox.Text.Trim(),
            Fullscreen = fullscreenBox.Checked,
            ExitOnWindowClose = exitOnCloseBox.Checked,
            AutoStart = autoStartBox.Checked,
        };
        s.SetExtraArgs(extraBox.Text.Trim());
        return s;
    }

    bool IsDirty()
    {
        var a = ReadUi(); var b = settings;
        return a.Name != b.Name || a.VideoSink != b.VideoSink || a.AudioSink != b.AudioSink || a.Fullscreen != b.Fullscreen ||
               a.ExtraArgs != b.ExtraArgs || a.ExitOnWindowClose != b.ExitOnWindowClose || a.AutoStart != b.AutoStart;
    }

    // Name and output/argument changes need a restart; the checkboxes apply immediately.
    bool NeedsRestart(Settings a)
    {
        return a.BuildArgs() != settings.BuildArgs();
    }

    void UpdateSaveButton()
    {
        bool dirty = IsDirty();
        save.Enabled = dirty;
        save.Text = dirty && IsRunning && NeedsRestart(ReadUi()) ? "Save and restart" : "Save";
    }

    void SaveFromUi()
    {
        var updated = ReadUi();
        bool restart = IsRunning && NeedsRestart(updated);
        settings = updated;
        if (!TrySaveSettings()) return;
        LoadSettingsIntoUi(); // reflect normalisation (e.g. -vs typed into Extra arguments)
        if (restart)
        {
            StopReceiver();
            FinishProcess();
            StartReceiver();
        }
        UpdateStatus();
    }

    bool TrySaveSettings()
    {
        try { settings.Save(iniPath); return true; }
        catch (Exception ex)
        {
            ShowError("Could not save settings to " + iniPath + ":\n\n" + ex.Message);
            return false;
        }
    }

    // ----- Runtime extraction -----

    void EnsureRuntime()
    {
        string versionFile = Path.Combine(runtimeDir, ".version");
        if (File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == BuildInfo.RuntimeHash && File.Exists(uxplayExe))
            return;
        if (Process.GetProcessesByName("uxplay").Length > 0)
            throw new IOException("uxplay.exe is already running. Stop it and reopen this app.");

        string tmp = runtimeDir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("runtime.zip"))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            zip.ExtractToDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, ".version"), BuildInfo.RuntimeHash);

        if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);
        Directory.Move(tmp, runtimeDir);
    }

    // ----- Firewall -----

    static int RunHidden(string file, string args, bool elevated)
    {
        var psi = new ProcessStartInfo(file, args) { WindowStyle = ProcessWindowStyle.Hidden };
        if (elevated) { psi.UseShellExecute = true; psi.Verb = "runas"; }
        else { psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; }
        using (var p = Process.Start(psi))
        {
            if (!elevated) p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    static bool FirewallRuleExists()
    {
        try { return RunHidden("netsh", "advfirewall firewall show rule name=\"" + FirewallRule + "\"", false) == 0; }
        catch (Exception) { return true; } // can't check; don't nag
    }

    void AddFirewallRule()
    {
        var r = MessageBox.Show(this,
            "Windows Firewall needs a rule so your iPhone can find this PC.\n\nAdd it now? (requires an administrator prompt)",
            Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r != DialogResult.Yes) return;
        try
        {
            RunHidden("netsh", "advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow" +
                " program=\"" + uxplayExe + "\" profile=private enable=yes", true);
        }
        catch (System.ComponentModel.Win32Exception) { } // admin prompt declined
        firewallRow.Visible = !FirewallRuleExists();
    }

    // ----- UxPlay process -----

    bool IsRunning { get { return proc != null && !proc.HasExited; } }

    void StartReceiver()
    {
        if (!runtimeReady || IsRunning) return;
        if (Process.GetProcessesByName("uxplay").Length > 0)
        {
            ShowError("Another copy of uxplay.exe is already running (perhaps started by start-receiver.bat). Stop it first.");
            return;
        }

        var psi = new ProcessStartInfo(uxplayExe, settings.BuildArgs())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.Combine(runtimeDir, "bin"),
        };
        // Point GStreamer only at the bundled plugins, never a system install.
        var env = psi.EnvironmentVariables;
        env["PATH"] = Path.Combine(runtimeDir, "bin") + ";" + env["PATH"];
        env["GST_PLUGIN_SYSTEM_PATH_1_0"] = Path.Combine(runtimeDir, "lib", "gstreamer-1.0");
        env["GST_PLUGIN_PATH_1_0"] = "";
        env["GST_PLUGIN_SCANNER_1_0"] = Path.Combine(runtimeDir, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
        env["GST_REGISTRY_1_0"] = Path.Combine(runtimeDir, "registry.bin");

        try
        {
            log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            var p = new Process { StartInfo = psi };
            var writer = log;
            DataReceivedEventHandler write = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) lock (writer) writer.WriteLine(e.Data);
            };
            p.OutputDataReceived += write;
            p.ErrorDataReceived += write;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            proc = p;
        }
        catch (Exception ex)
        {
            ShowError("Could not start uxplay.exe:\n\n" + ex.Message);
            return;
        }
        started = DateTime.Now;
        stopping = false;
        sawWindow = false;
        UpdateStatus();
    }

    void StopReceiver()
    {
        stopping = true;
        try { if (IsRunning) proc.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    // Waits for an exited/killed uxplay to finish writing its output, then closes the log.
    void FinishProcess()
    {
        if (proc == null) return;
        proc.WaitForExit();
        proc = null;
        lock (log) log.Close();
    }

    void OnTick()
    {
        if (proc == null) return;
        if (proc.HasExited)
        {
            FinishProcess();
            // Exiting within a few seconds of launch means UxPlay failed to start.
            if (!stopping && (DateTime.Now - started).TotalSeconds < 5)
                ShowError("UxPlay exited right after starting:\n\n" + LogTail(15));
            UpdateStatus();
            // With the window in the tray, a stopped receiver closes the app (as receiver.ps1 did).
            if (!Visible) ExitApp();
            return;
        }
        proc.Refresh();
        if (proc.MainWindowHandle != IntPtr.Zero)
        {
            if (!sawWindow) { sawWindow = true; UpdateStatus(); }
        }
        else if (sawWindow)
        {
            sawWindow = false;
            UpdateStatus();
            if (settings.ExitOnWindowClose) StopReceiver();
        }
    }

    string LogTail(int lines)
    {
        try
        {
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                return string.Join("\n", sr.ReadToEnd().Split('\n').Reverse().Take(lines).Reverse());
        }
        catch (IOException) { return "(log unavailable)"; }
    }

    // ----- Status display -----

    void UpdateStatus()
    {
        if (!runtimeReady) return;
        if (!IsRunning)
            SetStatus(Gray, "Stopped", "The receiver is not running.");
        else if (sawWindow)
            SetStatus(Blue, "Mirroring", "An iPhone is mirroring to “" + settings.Name + "”.");
        else
            SetStatus(Green, "Running", "Waiting for an iPhone. Control Center → Screen Mirroring → “" + settings.Name + "”.");
        startStop.Enabled = true;
        startStop.Text = IsRunning ? "Stop" : "Start";
        trayStartStop.Text = IsRunning ? "Stop receiver" : "Start receiver";
        UpdateSaveButton();
    }

    void SetStatus(Color color, string text, string detail)
    {
        dot.ForeColor = color;
        statusText.Text = text;
        statusDetail.Text = detail;
        var tip = Program.Title + ": " + text;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    void ShowError(string msg)
    {
        MessageBox.Show(Visible ? this : null, msg, Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static void OpenInNotepad(string path)
    {
        if (File.Exists(path)) Process.Start("notepad.exe", "\"" + path + "\"");
    }
}
