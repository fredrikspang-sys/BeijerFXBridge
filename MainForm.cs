using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace BeijerFXBridge;

sealed class MainForm : Form
{
    const int MaxLogLines = 1000;
    readonly Dictionary<string, string> cfg = Config.Load();
    readonly ComboBox host; readonly TextBox port, com, plc, log;
    readonly ComboBox proto, baud, parity, bits, stop;
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, Text = "Stopped", Padding = new Padding(8, 4, 8, 0) };
    readonly Button btnStart = new() { Text = "Start bridge", AutoSize = true };
    readonly Button btnStop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    readonly Button btnFind = new() { Text = "Find", Width = 60, Dock = DockStyle.Right };
    CancellationTokenSource findCts;
    Bridge bridge;

    public MainForm()
    {
        Text = "Beijer FX Bridge";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(560, 560);
        MinimumSize = new Size(540, 500);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Text = cfg["host"], Dock = DockStyle.Fill };
        var hostRow = Row(grid, "Panel IP", new Panel { Height = 24, Margin = new Padding(3, 2, 3, 2) });
        hostRow.Controls.Add(host); hostRow.Controls.Add(btnFind);
        new ToolTip().SetToolTip(btnFind, "Quick read-only scan of the local Ethernet/Wi-Fi subnets for the panel");
        port = Row(grid, "Port", new TextBox { Text = cfg["port"] });
        proto = Row(grid, "Protocol", Combo(cfg["protocol"].ToUpperInvariant(), "UDP", "TCP"));
        baud = Row(grid, "Baud", Combo(cfg["baud"], "9600", "19200", "38400", "57600", "115200"));
        com = Row(grid, "This app COM", new TextBox { Text = cfg["com"] });
        plc = Row(grid, "PLC software COM", new TextBox { Text = cfg["plc_com"] });
        parity = Row(grid, "Parity", Combo(cfg["parity"].ToUpperInvariant(), "E", "N", "O"));
        bits = Row(grid, "Data bits", Combo(cfg["bytesize"], "7", "8"));
        stop = Row(grid, "Stop bits", Combo(((int)double.Parse(cfg["stopbits"], CultureInfo.InvariantCulture)).ToString(), "1", "2"));

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 0, 8, 0),
            Text = "Classic Beijer transparent is usually UDP :6004. Use TCP only if E-Designer shows TCP for transparent. " +
                   "E-Designer 'TCP/IP connection' (IFC ETTP) is the panel NIC - not the same as this Protocol box.",
        };
        log = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, MaxLength = 0,
            ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window,
        };

        var save = new Button { Text = "Save settings", AutoSize = true };
        var clear = new Button { Text = "Clear log", AutoSize = true };
        var probe = new Button { Text = "Probe panel", AutoSize = true };
        var quit = new Button { Text = "Quit", AutoSize = true, Dock = DockStyle.Right };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        flow.Controls.AddRange(new Control[] { save, btnStart, btnStop, probe, clear });
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
        bar.Controls.Add(flow);
        bar.Controls.Add(quit);
        Controls.AddRange(new Control[] { log, hint, grid, status, bar }); // last added docks first

        save.Click += (_, _) => { Config.Save(Collect()); AppendLog($"Saved {Config.FilePath}"); };
        btnStart.Click += (_, _) => StartBridge();
        btnStop.Click += (_, _) => StopBridge();
        clear.Click += (_, _) => log.Clear();
        probe.Click += (_, _) => OpenProbe();
        btnFind.Click += async (_, _) => await FindPanel();
        host.SelectedIndexChanged += (_, _) =>
        {
            if (host.SelectedItem is not ProbeForm.Candidate c) return;
            if (c.Protocol != null) proto.SelectedItem = c.Protocol;
            BeginInvoke(new Action(() => { host.Text = c.Ip; host.Select(host.Text.Length, 0); }));
        };
        quit.Click += (_, _) => Close();
        FormClosing += (_, _) => { bridge?.Dispose(); bridge = null; };
        AppendLog($"Config: {Config.FilePath}");
    }

    static T Row<T>(TableLayoutPanel grid, string label, T ctrl) where T : Control
    {
        ctrl.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(ctrl);
        return ctrl;
    }

    static ComboBox Combo(string value, params string[] items)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        cb.Items.AddRange(items);
        if (!cb.Items.Contains(value)) cb.Items.Add(value);
        cb.SelectedItem = value;
        return cb;
    }

    // Thread-safe: bridge threads call this; the UI update is marshalled to the UI thread.
    void AppendLog(string msg)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AppendLog(msg))); } catch (InvalidOperationException) { } // form closing
            return;
        }
        log.AppendText(msg + Environment.NewLine);
        int extra = log.GetLineFromCharIndex(log.TextLength) - MaxLogLines;
        if (extra > 0)
        {
            log.Select(0, log.GetFirstCharIndexFromLine(extra));
            log.SelectedText = "";
            log.Select(log.TextLength, 0);
            log.ScrollToCaret();
        }
    }

    Dictionary<string, string> Collect() => new(cfg) // keeps bind, local_port, verbose from config.ini
    {
        ["com"] = com.Text.Trim(),
        ["plc_com"] = plc.Text.Trim(),
        ["host"] = HostText(),
        ["port"] = port.Text.Trim(),
        ["protocol"] = proto.Text.ToLowerInvariant(),
        ["baud"] = baud.Text,
        ["parity"] = parity.Text,
        ["bytesize"] = bits.Text,
        ["stopbits"] = stop.Text,
    };

    // Network-only probe; never touches COM ports or the running bridge.
    void OpenProbe()
    {
        int.TryParse(port.Text.Trim(), out int p);
        if (p <= 0 || p > 65535) p = 6004;
        using var f = new ProbeForm(HostText(), p, (ip, protocol) =>
        {
            host.Text = ip;
            if (protocol != null) proto.SelectedItem = protocol;
            AppendLog($"Probe: panel IP set to {ip}" + (protocol != null ? $" ({protocol})" : "") + " - press Save settings or Start bridge to keep it");
        });
        f.ShowDialog(this);
    }

    // Typed text, or just the IP part if a "IP  (name / reason)" entry is shown.
    string HostText() => (host.Text.Trim().Split(new[] { ' ' }, 2)[0]);

    async System.Threading.Tasks.Task FindPanel()
    {
        if (findCts != null) { findCts.Cancel(); return; }
        int.TryParse(port.Text.Trim(), out int p);
        if (p <= 0 || p > 65535) p = 6004;
        findCts = new CancellationTokenSource();
        string before = status.Text;
        btnFind.Text = "Cancel"; status.Text = "Finding panel on the local network...";
        UseWaitCursor = true; btnFind.UseWaitCursor = false; btnFind.Cursor = Cursors.Default;
        try
        {
            string dbg = "";
            var hits = await ProbeForm.QuickFind(p, HostText(), findCts.Token, x => dbg = x);
            host.Items.Clear();
            if (hits.Count == 0) { status.Text = $"Find: no likely panel found on the local network ({dbg})"; AppendLog(status.Text); return; }
            if (hits.Count == 1)
            {
                host.Text = hits[0].Ip;
                if (hits[0].Protocol != null) proto.SelectedItem = hits[0].Protocol;
                status.Text = $"Find: panel {hits[0]}  ({dbg})";
            }
            else
            {
                host.Items.AddRange(hits.Cast<object>().ToArray());
                host.SelectedIndex = 0; // best first
                status.Text = $"Find: {hits.Count} candidates - pick one in the Panel IP list ({dbg})";
                host.DroppedDown = true;
            }
            AppendLog(status.Text + " - press Save settings or Start bridge to keep it");
        }
        catch (OperationCanceledException) { status.Text = before; AppendLog("Find cancelled"); }
        catch (Exception e) { status.Text = "Find failed: " + e.Message; }
        finally { findCts.Dispose(); findCts = null; btnFind.Text = "Find"; UseWaitCursor = false; }
    }

    void StartBridge()
    {
        if (bridge != null) { AppendLog("Already running"); return; }
        var d = Collect();
        Config.Save(d);
        try { bridge = new Bridge(d, AppendLog); }
        catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        bridge.Start();
        status.Text = $"Running  {d["com"]}  {d["protocol"].ToUpperInvariant()} {d["host"]}:{d["port"]}  {d["baud"]} {d["bytesize"]}{d["parity"]}{d["stopbits"]}";
        btnStart.Enabled = false;
        btnStop.Enabled = true;
    }

    void StopBridge()
    {
        bridge?.Dispose();
        bridge = null;
        status.Text = "Stopped";
        btnStart.Enabled = true;
        btnStop.Enabled = false;
        AppendLog("Stopped");
    }
}
