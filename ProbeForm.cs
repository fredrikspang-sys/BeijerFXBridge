using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BeijerFXBridge;

// Read-only network probe: ICMP ping, TCP connect, empty UDP datagram, optional HTTP GET /.
// Never opens COM ports and never sends PLC protocol bytes.
sealed class ProbeForm : Form
{
    const int MaxHostsPerTarget = 1024, Parallel = 64;
    // IEEE MA-L registry entries for Beijer Electronics (checked against standards-oui.ieee.org/oui/oui.txt).
    static readonly Dictionary<string, string> BeijerOui = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00-12-77"] = "Beijer Electronics Corp.",
        ["00-50-6C"] = "Beijer Electronics Products AB",
    };
    // Other automation vendors (IEEE MA-L assignments, verified against the IEEE/Wireshark OUI list).
    static readonly Dictionary<string, string> PlcOui = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00-1B-1B"] = "Siemens", ["00-0E-8C"] = "Siemens", ["08-00-06"] = "Siemens", ["28-63-36"] = "Siemens",
        ["AC-64-17"] = "Siemens", ["8C-F3-19"] = "Siemens", ["E0-DC-A0"] = "Siemens",
        ["00-26-92"] = "Mitsubishi Electric", ["38-E0-8E"] = "Mitsubishi Electric",
        ["00-00-BC"] = "Rockwell Automation", ["00-1D-9C"] = "Rockwell Automation", ["F4-54-33"] = "Rockwell Automation",
        ["5C-88-16"] = "Rockwell Automation", ["E4-90-69"] = "Rockwell Automation", ["34-C0-F9"] = "Rockwell Automation",
        ["00-00-0A"] = "Omron",
        ["00-80-F4"] = "Schneider (Telemecanique)", ["00-00-54"] = "Schneider Electric",
        ["00-A0-45"] = "Phoenix Contact",
        ["00-30-DE"] = "WAGO",
        ["00-90-E8"] = "Moxa",
    };
    static readonly (int Port, string Name)[] PlcPorts = { (102, "Siemens S7"), (502, "Modbus TCP"), (5007, "Mitsubishi SLMP"), (44818, "EtherNet/IP") };

    readonly string cfgHost; readonly int cfgPort;
    readonly Action<string, string> useHost;
    readonly CheckedListBox ifaces = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly CheckBox showAll = new() { Text = "Show all hosts (not only likely panels)", AutoSize = true };
    readonly CheckBox http = new() { Text = "Read HTTP title/Server on port 80", AutoSize = true, Checked = true };
    readonly CheckBox identity = new() { Text = "Read device identity (Modbus 43/14, EtherNet/IP ListIdentity)", AutoSize = true };
    readonly TextBox extra = new() { Width = 260 };
    readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    readonly Label info = new() { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(6, 2, 6, 0) };
    readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 14 };
    readonly Button btnScan = new() { Text = "Scan", AutoSize = true };
    readonly Button btnCancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    readonly Button btnUse = new() { Text = "Use", AutoSize = true, Enabled = false };
    readonly List<Iface> found = new();
    List<Result> lastResults = new();
    CancellationTokenSource cts;

    sealed class Iface { public string Name, Desc; public int Index; public uint Addr, Mask; public bool External, Virtual, Physical; public string Label; public override string ToString() => Label; }
    sealed class Target { public uint First, Last; public bool External; public string Source; }
    sealed class Result { public bool Configured; public string Name = ""; public string Device = ""; public bool Plc; public string Ip, Mac = "", Vendor = "", Tcp = "", Udp = "", Http = "", Source = ""; public long Ms = -1; public bool Panel; }

    public ProbeForm(string host, int port, Action<string, string> useHost)
    {
        cfgHost = host; cfgPort = port; this.useHost = useHost;
        Text = $"Probe panel (port {port})";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(900, 600); MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterParent;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 175, ColumnCount = 2, Padding = new Padding(6) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        opts.Controls.Add(showAll); opts.Controls.Add(http); opts.Controls.Add(identity);
        opts.Controls.Add(new Label { Text = "Extra range (CIDR or a.b.c.d-e):", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        opts.Controls.Add(extra);
        var btns = new FlowLayoutPanel { AutoSize = true }; btns.Controls.AddRange(new Control[] { btnScan, btnCancel, btnUse });
        opts.Controls.Add(btns);
        top.Controls.Add(ifaces); top.Controls.Add(opts);

        foreach (var (t, w) in new[] { ("IP", 110), ("Hostname", 130), ("Likely panel", 85), ("Device type", 190), ("MAC", 125), ("Ping ms", 60), ($"TCP {port}", 70), ($"UDP {port}", 95), ("HTTP :80", 170), ("Via", 150) })
            list.Columns.Add(t, w);
        info.Text = "MAC/vendor detection only works on the local L2 network (not through routers or VPNs). " +
                    "Probe sends ping, TCP connects, one empty UDP datagram and HTTP GET / only; identity reads are opt-in and read-only. Never writes.";
        Controls.AddRange(new Control[] { list, top, progress, info });

        showAll.CheckedChanged += (_, _) => Render();
        btnScan.Click += async (_, _) => await Scan();
        btnCancel.Click += (_, _) => cts?.Cancel();
        btnUse.Click += (_, _) => Use();
        list.DoubleClick += (_, _) => Use();
        list.SelectedIndexChanged += (_, _) => btnUse.Enabled = list.SelectedItems.Count > 0;
        FormClosing += (_, _) => cts?.Cancel();
        LoadInterfaces();
        Shown += async (_, _) => { if (!string.IsNullOrWhiteSpace(cfgHost)) { await Scan(hostOnly: true); info.Text = "Checked the configured host only. Press Scan to sweep the ticked adapters. " + info.Text; } };
        // Tick adapters (Tailscale/ZeroTier/VPN too) and press Scan to sweep their subnets, routes and peers.
    }

    void Use()
    {
        if (list.SelectedItems.Count == 0) return;
        var r = (Result)list.SelectedItems[0].Tag;
        string proto = r.Tcp == "open" ? "TCP" : r.Udp == "open" ? "UDP" : null; // reply over UDP or a TCP accept
        if (proto == null && r.Udp.StartsWith("open")) proto = "UDP";
        useHost(r.Ip, proto);
        Close();
    }

    void LoadInterfaces()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPv4InterfaceProperties p4; try { p4 = ni.GetIPProperties().GetIPv4Properties(); } catch { continue; }
            string s = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool ext = Regex.IsMatch(s, "tailscale|zerotier|vpn|wireguard|openvpn|tap-|wintun|hamachi|fortinet|anyconnect");
            bool virt = Regex.IsMatch(s, "hyper-v|vethernet|virtualbox|vmware|wsl|docker|loopback|bluetooth");
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                // IPv4Mask is unreliable on .NET Framework (can be 0.0.0.0/255.255.255.255); prefer PrefixLength.
                int pl = 0; try { pl = ua.PrefixLength; } catch { }
                uint a = ToU(ua.Address), m = pl > 0 && pl <= 32 ? 0xFFFFFFFF << (32 - pl) : ua.IPv4Mask != null ? ToU(ua.IPv4Mask) : 0;
                if (m == 0 || (m == 0xFFFFFFFF && !(ext || (a & 0xFFC00000) == 0x64400000))) m = 0xFFFFFF00; // unknown/host mask on a LAN adapter: assume /24
                if ((a >> 24) == 169 && ((a >> 16) & 255) == 254) continue; // APIPA
                bool cgnat = (a & 0xFFC00000) == 0x64400000; // 100.64.0.0/10 (Tailscale)
                var f = new Iface { Name = ni.Name, Desc = ni.Description, Index = p4?.Index ?? -1, Addr = a, Mask = m, External = ext || cgnat, Virtual = virt,
                    Physical = ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT };
                f.Label = $"{ni.Name}  {ua.Address}/{Prefix(m)}" + (f.External ? "  [external]" : f.Virtual ? "  [virtual]" : "");
                found.Add(f);
                ifaces.Items.Add(f, !f.External && !f.Virtual);
            }
        }
    }

    List<Target> BuildTargets()
    {
        var t = new List<Target>();
        for (int i = 0; i < found.Count; i++)
        {
            if (!ifaces.CheckedIndices.Contains(i)) continue;
            var f = found[i];
            if (f.Mask != 0xFFFFFFFF) t.Add(Net(f.Addr, f.Mask, f.External, f.Name));
            if (f.External && f.Index >= 0)
            {
                foreach (var (dst, mask) in RoutesVia(f.Index))
                    if (mask != 0 && Prefix(mask) >= 16 && Prefix(mask) < 32 && (dst & 0xFFC00000) != 0x64400000 && (dst >> 24) != 224)
                        t.Add(Net(dst, mask, true, f.Name + " route"));
                if (f.Desc.IndexOf("tailscale", StringComparison.OrdinalIgnoreCase) >= 0 || (f.Addr & 0xFFC00000) == 0x64400000)
                    foreach (var p in TailscalePeers()) t.Add(new Target { First = p, Last = p, External = true, Source = "Tailscale peer" });
            }
        }
        foreach (var part in extra.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var r = ParseRange(part.Trim());
            if (r == null) throw new FormatException($"Cannot parse range '{part}'. Use 192.168.3.0/24 or 10.0.2.1-50.");
            t.Add(r);
        }
        return t;
    }

    static Target Net(uint addr, uint mask, bool ext, string src)
    {
        uint net = addr & mask, bc = net | ~mask;
        uint first = mask >= 0xFFFFFFFE ? net : net + 1, last = mask >= 0xFFFFFFFE ? bc : bc - 1;
        if (last - first + 1 > MaxHostsPerTarget) // centre the window on our own address
        {
            uint s = Math.Max(first, addr > MaxHostsPerTarget / 2 ? addr - MaxHostsPerTarget / 2 : first);
            first = Math.Min(s, last - MaxHostsPerTarget + 1); last = first + MaxHostsPerTarget - 1;
            src += " (capped)";
        }
        return new Target { First = first, Last = last, External = ext, Source = src };
    }

    static Target ParseRange(string s)
    {
        var m = Regex.Match(s, @"^(\d+\.\d+\.\d+\.\d+)/(\d{1,2})$");
        if (m.Success && IPAddress.TryParse(m.Groups[1].Value, out var ip) && int.Parse(m.Groups[2].Value) <= 32)
        {
            int p = int.Parse(m.Groups[2].Value);
            uint mask = p == 0 ? 0 : 0xFFFFFFFF << (32 - p);
            return Net(ToU(ip), mask, true, "extra " + s);
        }
        m = Regex.Match(s, @"^(\d+\.\d+\.\d+\.\d+)(?:-(\d+(?:\.\d+\.\d+\.\d+)?))?$");
        if (!m.Success || !IPAddress.TryParse(m.Groups[1].Value, out var a)) return null;
        uint first = ToU(a), last = first;
        if (m.Groups[2].Success)
        {
            string e = m.Groups[2].Value;
            if (e.Contains('.')) { if (!IPAddress.TryParse(e, out var b)) return null; last = ToU(b); }
            else { int n = int.Parse(e); if (n > 255) return null; last = (first & 0xFFFFFF00) | (uint)n; }
        }
        if (last < first) return null;
        if (last - first + 1 > MaxHostsPerTarget) last = first + MaxHostsPerTarget - 1;
        return new Target { First = first, Last = last, External = true, Source = "extra " + s };
    }

    async Task Scan(bool hostOnly = false)
    {
        List<Target> targets;
        try
        {
            if (hostOnly)
            {
                var ip = Dns.GetHostAddresses(cfgHost).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) { info.Text = $"Cannot resolve {cfgHost}"; return; }
                targets = new() { new Target { First = ToU(ip), Last = ToU(ip), External = true, Source = "configured host" } };
            }
            else
            {
                targets = BuildTargets();
                if (!string.IsNullOrWhiteSpace(cfgHost) && IPAddress.TryParse(cfgHost, out var ch))
                    targets.Insert(0, new Target { First = ToU(ch), Last = ToU(ch), External = true, Source = "configured host" });
            }
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        var ips = new Dictionary<uint, Target>();
        foreach (var t in targets) for (ulong a = t.First; a <= t.Last; a++) if (!ips.ContainsKey((uint)a)) ips[(uint)a] = t;
        foreach (var f in found) ips.Remove(f.Addr); // skip our own addresses
        if (ips.Count == 0) { info.Text = "Nothing to scan: tick an interface or enter a range."; return; }

        cts = new CancellationTokenSource(); var ct = cts.Token;
        btnScan.Enabled = false; btnCancel.Enabled = true; btnUse.Enabled = false;
        list.Items.Clear();
        progress.Maximum = ips.Count; progress.Value = 0;
        bool doHttp = http.Checked, doId = identity.Checked;
        var results = new List<Result>();
        var gate = new SemaphoreSlim(Parallel);
        int done = 0;
        info.Text = $"Scanning {ips.Count} addresses: " + string.Join(", ", targets.Select(t => $"{ToIp(t.First)}-{ToIp(t.Last)} ({t.Source})"));
        var tasks = ips.Select(async kv =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool isCfg = kv.Value.Source == "configured host";
                var r = await ProbeHost(ToIp(kv.Key), kv.Value.External, doHttp, doId, kv.Value.Source, isCfg, ct).ConfigureAwait(false);
                if (r != null) r.Configured = isCfg;
                if (r != null) lock (results) results.Add(r);
            }
            catch (OperationCanceledException) { }
            finally { gate.Release(); int d = Interlocked.Increment(ref done); if (d % 8 == 0 || d == ips.Count) BeginInvokeSafe(() => progress.Value = Math.Min(d, progress.Maximum)); }
        }).ToList();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }

        var arp = await Task.Run(ArpTable);
        foreach (var r in results)
            if (arp.TryGetValue(r.Ip, out var mac))
            {
                r.Mac = mac;
                if (BeijerOui.TryGetValue(mac.Substring(0, 8), out var v)) { r.Vendor = v; r.Panel = true; r.Device = Join("Beijer", r.Device); }
                else if (PlcOui.TryGetValue(mac.Substring(0, 8), out v)) { r.Vendor = v; r.Plc = true; r.Device = Join(v, r.Device); }
            }
        foreach (var r in results) r.Source = ViaFor(r.Ip, r.Source);
        lastResults = results;
        Render();
        _ = ResolveNames(results); // fills Hostname column in the background
        info.Text = (ct.IsCancellationRequested ? "Cancelled. " : "Done. ") + $"{results.Count} responder(s) of {ips.Count} scanned, {list.Items.Count} shown. " +
                    "MAC/vendor detection only works on the local L2 network. Double-click or press Use to set the panel IP.";
        btnScan.Enabled = true; btnCancel.Enabled = false;
        cts.Dispose(); cts = null;
    }

    public sealed class Candidate { public string Ip, Name, Reason, Protocol; public override string ToString() => $"{Ip}  ({(Name != "" ? Name + " / " : "")}{Reason})"; }

    // Quick LAN-only search used by the main window's Find button: up physical Ethernet/Wi-Fi adapters only,
    // same read-only probe, returns likely panels best first.
    public static async Task<List<Candidate>> QuickFind(int port, CancellationToken ct)
    {
        using var f = new ProbeForm("", port, (_, _) => { });
        var targets = f.found.Where(x => x.Physical && !x.External && !x.Virtual && x.Mask != 0xFFFFFFFF).Select(x => Net(x.Addr, x.Mask, false, x.Name)).ToList();
        var ips = new Dictionary<uint, Target>();
        foreach (var t in targets) for (ulong a = t.First; a <= t.Last; a++) if (!ips.ContainsKey((uint)a)) ips[(uint)a] = t;
        foreach (var x in f.found) ips.Remove(x.Addr);
        var results = new List<Result>(); var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(ips.Select(async kv =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { var r = await f.ProbeHost(ToIp(kv.Key), false, true, false, kv.Value.Source, false, ct).ConfigureAwait(false); if (r != null) lock (results) results.Add(r); }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var arp = ArpTable();
        foreach (var r in results)
            if (arp.TryGetValue(r.Ip, out var mac)) { r.Mac = mac; if (BeijerOui.ContainsKey(mac.Substring(0, 8))) r.Panel = true; }
        var hits = results.Where(r => Likely(r) != "").OrderBy(Rank).ThenBy(r => ToU(IPAddress.Parse(r.Ip))).ToList();
        await Task.WhenAll(hits.Select(async r =>
        {
            r.Name = await ReverseDns(r.Ip, 1000).ConfigureAwait(false) ?? await NetBiosName(r.Ip, 600).ConfigureAwait(false)
                  ?? await DnsPtrUdp(r.Ip, 5353, 600).ConfigureAwait(false) ?? await DnsPtrUdp(r.Ip, 5355, 600).ConfigureAwait(false) ?? "";
        })).ConfigureAwait(false);
        return hits.Select(r => new Candidate
        {
            Ip = r.Ip, Name = r.Name, Reason = Likely(r),
            Protocol = r.Tcp == "open" ? "TCP" : r.Udp.StartsWith("open") ? "UDP" : null,
        }).ToList();
    }

    static string Likely(Result r) =>
        r.Panel ? "YES (Beijer MAC)" : r.Tcp == "open" ? "likely (TCP)" :
        (r.Udp == "open" || (r.Udp == "open|filtered" && (r.Http == "" || r.Http.StartsWith("err") || r.Http == "timeout"))) ? "maybe (UDP)" : "";

    static int Rank(Result r) => r.Configured ? 0 : r.Panel ? 1 : r.Tcp == "open" ? 2 : Likely(r) != "" ? 3 : r.Plc ? 4 : 5;
    static bool Interesting(Result r) => r.Configured || r.Plc || Likely(r) != "";
    static string Join(string a, string b) => b == "" ? a : a + "; " + b;

    void Render()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var r in lastResults.Where(r => showAll.Checked || Interesting(r))
                     .OrderBy(Rank).ThenBy(r => ToU(IPAddress.Parse(r.Ip))))
        {
            var it = new ListViewItem(new[] { r.Ip + (r.Configured ? " *" : ""), r.Name, Likely(r), r.Device, r.Mac, r.Ms >= 0 ? r.Ms.ToString() : "-", r.Tcp, r.Udp, r.Http, r.Source }) { Tag = r };
            if (r.Panel) it.Font = new Font(list.Font, FontStyle.Bold);
            list.Items.Add(it);
        }
        list.EndUpdate();
        btnUse.Enabled = false;
    }

    // Label a result with the adapter whose subnet contains it (routing may still go another way).
    string ViaFor(string ip, string fallback)
    {
        uint a = ToU(IPAddress.Parse(ip));
        var f = found.FirstOrDefault(x => x.Mask != 0xFFFFFFFF && (a & x.Mask) == (x.Addr & x.Mask) && !x.External)
             ?? found.FirstOrDefault(x => x.Mask != 0xFFFFFFFF && (a & x.Mask) == (x.Addr & x.Mask));
        return f != null ? f.Name : fallback;
    }

    void BeginInvokeSafe(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch (InvalidOperationException) { } }

    async Task<Result> ProbeHost(IPAddress ip, bool slow, bool doHttp, bool readId, string src, bool always, CancellationToken ct)
    {
        int pingMs = slow ? 1500 : 400, tcpMs = slow ? 1500 : 500, udpMs = slow ? 1500 : 600;
        var r = new Result { Ip = ip.ToString(), Source = src };
        try { using var p = new Ping(); var rep = await p.SendPingAsync(ip, pingMs).ConfigureAwait(false); if (rep.Status == IPStatus.Success) r.Ms = rep.RoundtripTime; } catch { }
        ct.ThrowIfCancellationRequested();
        r.Tcp = await TcpCheck(ip, cfgPort, tcpMs).ConfigureAwait(false) switch { true => "open", false => "closed", _ => "no reply" };
        bool alive = r.Ms >= 0 || r.Tcp != "no reply";
        if (!alive && !always) return null; // quiet host: skip UDP/HTTP
        ct.ThrowIfCancellationRequested();
        r.Udp = await UdpCheck(ip, cfgPort, udpMs).ConfigureAwait(false);
        if (doHttp && await TcpCheck(ip, 80, tcpMs).ConfigureAwait(false) == true) r.Http = await HttpInfo(ip, slow ? 3000 : 1500).ConfigureAwait(false);
        if (r.Tcp == "no reply") r.Tcp = "filtered";
        var open = await Task.WhenAll(PlcPorts.Select(p => p.Port == cfgPort && r.Tcp == "open" ? Task.FromResult<bool?>(true) : TcpCheck(ip, p.Port, tcpMs))).ConfigureAwait(false);
        var dev = new List<string>();
        for (int i = 0; i < PlcPorts.Length; i++) if (open[i] == true) dev.Add($"{PlcPorts[i].Name} :{PlcPorts[i].Port}");
        if (readId)
        {
            if (open[1] == true) { var s = await ModbusIdentity(ip, tcpMs * 2).ConfigureAwait(false); if (s != null) dev.Add(s); }
            if (open[3] == true) { var s = await EnipIdentity(ip, tcpMs * 2).ConfigureAwait(false); if (s != null) dev.Add(s); }
        }
        r.Device = string.Join("; ", dev); r.Plc = dev.Count > 0;
        return r;
    }

    // true = accepted, false = refused (RST), null = timeout
    static async Task<bool?> TcpCheck(IPAddress ip, int port, int ms)
    {
        using var c = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            var t = c.ConnectAsync(ip, port);
            if (await Task.WhenAny(t, Task.Delay(ms)).ConfigureAwait(false) != t) { _ = t.ContinueWith(x => _ = x.Exception); return null; }
            await t.ConfigureAwait(false); return true;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return false; }
        catch { return null; }
    }

    // Sends one zero-length datagram (no payload, nothing for the panel to forward).
    // ICMP port-unreachable shows up as ConnectionReset on Windows -> closed; reply -> open; silence -> open|filtered.
    static async Task<string> UdpCheck(IPAddress ip, int port, int ms)
    {
        using var u = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            u.Connect(ip, port);
            await u.SendAsync(Array.Empty<byte>(), 0).ConfigureAwait(false);
            var t = u.ReceiveAsync();
            if (await Task.WhenAny(t, Task.Delay(ms)).ConfigureAwait(false) != t) { _ = t.ContinueWith(x => _ = x.Exception); return "open|filtered"; }
            await t.ConfigureAwait(false); return "open";
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { return "closed"; }
        catch { return "error"; }
    }

    static async Task<string> HttpInfo(IPAddress ip, int ms)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create($"http://{ip}/");
            req.Method = "GET"; req.Timeout = ms; req.ReadWriteTimeout = ms; req.AllowAutoRedirect = false; req.UserAgent = "BeijerFXBridge-probe";
            var t = req.GetResponseAsync();
            if (await Task.WhenAny(t, Task.Delay(ms)).ConfigureAwait(false) != t) { req.Abort(); return "timeout"; }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)await t.ConfigureAwait(false); }
            catch (WebException we) when (we.Response is HttpWebResponse er) { resp = er; }
            using (resp)
            {
                string server = resp.Headers["Server"] ?? "", title = "";
                using var s = resp.GetResponseStream();
                var buf = new byte[16384]; int n = 0, k;
                s.ReadTimeout = ms;
                while (n < buf.Length && (k = s.Read(buf, n, buf.Length - n)) > 0) n += k;
                var m = Regex.Match(Encoding.UTF8.GetString(buf, 0, n), @"<title[^>]*>\s*([^<]{0,80})", RegexOptions.IgnoreCase);
                if (m.Success) title = m.Groups[1].Value.Trim();
                return $"{(int)resp.StatusCode} {title} {(server != "" ? "[" + server + "]" : "")}".Trim();
            }
        }
        catch (Exception e) { return "err: " + e.GetType().Name; }
    }

    static IEnumerable<uint> TailscalePeers() => TailscaleNames().Keys.Select(ip => ToU(IPAddress.Parse(ip))).Take(MaxHostsPerTarget).ToList();

    // Peer IP -> host name from `tailscale status --json` (empty if the CLI is missing).
    static Dictionary<string, string> TailscaleNames()
    {
        var d = new Dictionary<string, string>();
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(exe)) exe = "tailscale.exe";
        string json;
        try
        {
            var psi = new ProcessStartInfo(exe, "status --json") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var pr = Process.Start(psi);
            var read = pr.StandardOutput.ReadToEndAsync();
            if (!pr.WaitForExit(4000)) { try { pr.Kill(); } catch { } return d; }
            json = read.Result;
        }
        catch { return d; }
        int peers = json.IndexOf("\"Peer\"", StringComparison.Ordinal);
        if (peers < 0) return d;
        foreach (Match m in Regex.Matches(json.Substring(peers), "\"HostName\"\\s*:\\s*\"([^\"]*)\"[\\s\\S]*?\"TailscaleIPs\"\\s*:\\s*\\[\\s*\"(100\\.[0-9.]+)\""))
            d[m.Groups[2].Value] = m.Groups[1].Value;
        return d;
    }

    async Task ResolveNames(List<Result> results)
    {
        Dictionary<string, string> ts = null;
        if (results.Any(r => r.Ip.StartsWith("100."))) ts = await Task.Run(TailscaleNames);
        var gate = new SemaphoreSlim(32);
        await Task.WhenAll(results.Select(async r =>
        {
            await gate.WaitAsync();
            try
            {
                string n = null;
                if (ts != null) ts.TryGetValue(r.Ip, out n);
                if (string.IsNullOrEmpty(n)) n = await ReverseDns(r.Ip, 1500);
                if (string.IsNullOrEmpty(n)) n = await NetBiosName(r.Ip, 800);
                if (string.IsNullOrEmpty(n)) n = await DnsPtrUdp(r.Ip, 5353, 800);  // mDNS (unicast)
                if (string.IsNullOrEmpty(n)) n = await DnsPtrUdp(r.Ip, 5355, 800);  // LLMNR (unicast)
                if (string.IsNullOrEmpty(n)) return;
                r.Name = n;
                BeginInvokeSafe(() => { foreach (ListViewItem it in list.Items) if (it.Tag == r) it.SubItems[1].Text = n; });
            }
            catch { }
            finally { gate.Release(); }
        }));
    }

    static async Task<string> ReverseDns(string ip, int ms)
    {
        try
        {
            var t = Dns.GetHostEntryAsync(IPAddress.Parse(ip));
            if (await Task.WhenAny(t, Task.Delay(ms)) != t) { _ = t.ContinueWith(x => _ = x.Exception); return null; }
            var h = (await t).HostName;
            return h == ip ? null : h;
        }
        catch { return null; }
    }

    // NetBIOS node status query (NBSTAT, read-only) to UDP 137.
    static async Task<string> NetBiosName(string ip, int ms)
    {
        var q = new byte[50];
        q[0] = 0x4E; q[1] = 0x42; q[5] = 1; // txid, QDCOUNT=1
        q[12] = 0x20; q[13] = (byte)'C'; q[14] = (byte)'K';
        for (int i = 15; i < 45; i++) q[i] = (byte)'A';
        q[46] = 0x00; q[47] = 0x21; q[48] = 0x00; q[49] = 0x01; // NBSTAT, IN (q[45]=0 terminator)
        using var u = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            u.Connect(IPAddress.Parse(ip), 137);
            await u.SendAsync(q, q.Length);
            var t = u.ReceiveAsync();
            if (await Task.WhenAny(t, Task.Delay(ms)) != t) { _ = t.ContinueWith(x => _ = x.Exception); return null; }
            var b = (await t).Buffer;
            if (b.Length < 57) return null;
            int n = b[56];
            for (int i = 0; i < n && 57 + i * 18 + 18 <= b.Length; i++)
            {
                int o = 57 + i * 18;
                if (b[o + 15] == 0x00 && (b[o + 16] & 0x80) == 0) return Encoding.ASCII.GetString(b, o, 15).Trim();
            }
            return null;
        }
        catch { return null; }
    }

    // PTR query for d.c.b.a.in-addr.arpa sent unicast to the host itself (mDNS on 5353, LLMNR on 5355).
    static async Task<string> DnsPtrUdp(string ip, int port, int ms)
    {
        var q = new List<byte> { 0x12, 0x34, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var l in ip.Split('.').Reverse().Concat(new[] { "in-addr", "arpa" })) { q.Add((byte)l.Length); q.AddRange(Encoding.ASCII.GetBytes(l)); }
        q.AddRange(new byte[] { 0, 0, 12, 0, 1 });
        using var u = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            u.Connect(IPAddress.Parse(ip), port);
            await u.SendAsync(q.ToArray(), q.Count);
            var t = u.ReceiveAsync();
            if (await Task.WhenAny(t, Task.Delay(ms)) != t) { _ = t.ContinueWith(x => _ = x.Exception); return null; }
            var b = (await t).Buffer;
            if (b.Length < 12 || (b[2] & 0x80) == 0) return null;
            int qd = b[4] << 8 | b[5], an = b[6] << 8 | b[7], o = 12;
            for (int i = 0; i < qd; i++) { DnsName(b, ref o); o += 4; }
            for (int i = 0; i < an && o + 10 <= b.Length; i++)
            {
                DnsName(b, ref o);
                int type = b[o] << 8 | b[o + 1], len = b[o + 8] << 8 | b[o + 9]; o += 10;
                if (type == 12) { int p = o; var n = DnsName(b, ref p); if (!string.IsNullOrEmpty(n)) return n.TrimEnd('.'); }
                o += len;
            }
            return null;
        }
        catch { return null; }
    }

    static string DnsName(byte[] b, ref int o)
    {
        var parts = new List<string>(); int p = o, jumps = 0; bool jumped = false;
        while (p < b.Length && b[p] != 0 && jumps < 16)
        {
            if ((b[p] & 0xC0) == 0xC0) { if (!jumped) o = p + 2; jumped = true; p = (b[p] & 0x3F) << 8 | b[p + 1]; jumps++; continue; }
            int l = b[p]; parts.Add(Encoding.UTF8.GetString(b, p + 1, Math.Min(l, b.Length - p - 1))); p += l + 1;
        }
        if (!jumped) o = p + 1;
        return string.Join(".", parts);
    }

    // --- Read-only identification (opt-in) ---
    static Task<byte[]> Exchange(IPAddress ip, int port, byte[] req, int ms, int hdr, Func<byte[], int> total) => Task.Run(() =>
    {
        try
        {
            using var c = new TcpClient(AddressFamily.InterNetwork);
            var ar = c.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ms)) return null;
            c.EndConnect(ar);
            c.ReceiveTimeout = ms; c.SendTimeout = ms;
            var s = c.GetStream();
            s.Write(req, 0, req.Length);
            var buf = new byte[1024]; int n = 0;
            while (true)
            {
                int need = n >= hdr ? Math.Min(buf.Length, total(buf)) : hdr;
                if (n >= need) break;
                int k = s.Read(buf, n, need - n); if (k <= 0) return null; n += k;
            }
            return buf.Take(n).ToArray();
        }
        catch { return null; }
    });

    // Modbus function 43 / MEI 14 Read Device Identification, basic objects (vendor, product code, revision). Read-only.
    static async Task<string> ModbusIdentity(IPAddress ip, int ms)
    {
        var req = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0xFF, 0x2B, 0x0E, 0x01, 0x00 };
        var b = await Exchange(ip, 502, req, ms, 7, x => 6 + (x[4] << 8 | x[5])).ConfigureAwait(false);
        if (b == null || b.Length < 9) return null;
        if (b[7] == 0xAB) return "Modbus ID: not supported";
        if (b[7] != 0x2B || b[8] != 0x0E || b.Length < 14) return null;
        var parts = new List<string>(); int o = 14;
        for (int i = 0; i < b[13] && o + 2 <= b.Length; i++)
        {
            int len = b[o + 1]; if (o + 2 + len > b.Length) break;
            parts.Add(Encoding.ASCII.GetString(b, o + 2, len).Trim()); o += 2 + len;
        }
        return parts.Count > 0 ? "Modbus ID: " + string.Join(" ", parts) : null;
    }

    // EtherNet/IP encapsulation ListIdentity (command 0x63). Read-only, no session.
    static async Task<string> EnipIdentity(IPAddress ip, int ms)
    {
        var req = new byte[24]; req[0] = 0x63;
        var b = await Exchange(ip, 44818, req, ms, 24, x => 24 + (x[2] | x[3] << 8)).ConfigureAwait(false);
        if (b == null || b.Length < 63 || b[0] != 0x63 || (b[24] | b[25] << 8) < 1) return null;
        int vendor = b[48] | b[49] << 8, product = b[52] | b[53] << 8, nl = b[62];
        string name = 63 + nl <= b.Length ? Encoding.ASCII.GetString(b, 63, nl).Trim() : "";
        string vn = vendor switch { 1 => "Rockwell", 47 => "Omron", _ => $"vendor {vendor}" };
        return $"EtherNet/IP: {vn} {name} rev {b[54]}.{b[55]} (product {product})".Replace("  ", " ");
    }

    // --- iphlpapi: route table and ARP table (read-only) ---
    [StructLayout(LayoutKind.Sequential)]
    struct MIB_IPFORWARDROW { public uint Dest, Mask, Policy, NextHop; public int IfIndex; public uint Type, Proto, Age, NextHopAS, Metric1, Metric2, Metric3, Metric4, Metric5; }
    [StructLayout(LayoutKind.Sequential)]
    struct MIB_IPNETROW { public int Index; public int PhysAddrLen; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Phys; public uint Addr; public int Type; }
    [DllImport("iphlpapi.dll")] static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);
    [DllImport("iphlpapi.dll")] static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    delegate int TableFn(IntPtr table, ref int size, bool order);

    static List<T> Table<T>(TableFn fn) where T : struct
    {
        var res = new List<T>();
        int size = 0;
        fn(IntPtr.Zero, ref size, false);
        if (size <= 0) return res;
        size += 4096; // table may grow between calls
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (fn(buf, ref size, false) != 0) return res;
            int n = Marshal.ReadInt32(buf), sz = Marshal.SizeOf<T>();
            for (int i = 0; i < n; i++) res.Add(Marshal.PtrToStructure<T>(buf + 4 + i * sz));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }

    static List<(uint, uint)> RoutesVia(int ifIndex)
    {
        try { return Table<MIB_IPFORWARDROW>(GetIpForwardTable).Where(r => r.IfIndex == ifIndex).Select(r => (Swap(r.Dest), Swap(r.Mask))).Distinct().ToList(); }
        catch { return new(); }
    }

    static Dictionary<string, string> ArpTable()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var r in Table<MIB_IPNETROW>(GetIpNetTable))
            {
                if (r.PhysAddrLen != 6 || r.Type == 2 /*invalid*/) continue;
                d[ToIp(Swap(r.Addr)).ToString()] = string.Join("-", r.Phys.Take(6).Select(b => b.ToString("X2")));
            }
        }
        catch { }
        return d;
    }

    static uint Swap(uint v) => (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
    static uint ToU(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    static IPAddress ToIp(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    static int Prefix(uint m) { int n = 0; while ((m & 0x80000000) != 0) { n++; m <<= 1; } return n; }
}
