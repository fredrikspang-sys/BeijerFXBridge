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

    readonly string cfgHost; readonly int cfgPort;
    readonly Action<string, string> useHost;
    readonly CheckedListBox ifaces = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly CheckBox external = new() { Text = "Include external networks (Tailscale/ZeroTier/VPN)", AutoSize = true };
    readonly CheckBox http = new() { Text = "Read HTTP title/Server on port 80", AutoSize = true, Checked = true };
    readonly TextBox extra = new() { Width = 260 };
    readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    readonly Label info = new() { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(6, 2, 6, 0) };
    readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 14 };
    readonly Button btnScan = new() { Text = "Scan", AutoSize = true };
    readonly Button btnCancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    readonly Button btnUse = new() { Text = "Use", AutoSize = true, Enabled = false };
    readonly List<Iface> found = new();
    CancellationTokenSource cts;

    sealed class Iface { public string Name, Desc; public int Index; public uint Addr, Mask; public bool External, Virtual; public string Label; public override string ToString() => Label; }
    sealed class Target { public uint First, Last; public bool External; public string Source; }
    sealed class Result { public string Ip, Mac = "", Vendor = "", Tcp = "", Udp = "", Http = "", Source = ""; public long Ms = -1; public bool Panel; }

    public ProbeForm(string host, int port, Action<string, string> useHost)
    {
        cfgHost = host; cfgPort = port; this.useHost = useHost;
        Text = $"Probe panel (port {port})";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(900, 600); MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterParent;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 150, ColumnCount = 2, Padding = new Padding(6) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        opts.Controls.Add(external); opts.Controls.Add(http);
        opts.Controls.Add(new Label { Text = "Extra range (CIDR or a.b.c.d-e):", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        opts.Controls.Add(extra);
        var btns = new FlowLayoutPanel { AutoSize = true }; btns.Controls.AddRange(new Control[] { btnScan, btnCancel, btnUse });
        opts.Controls.Add(btns);
        top.Controls.Add(ifaces); top.Controls.Add(opts);

        foreach (var (t, w) in new[] { ("IP", 110), ("Likely panel", 85), ("MAC", 125), ("Ping ms", 60), ($"TCP {port}", 70), ($"UDP {port}", 95), ("HTTP :80", 170), ("Via", 150) })
            list.Columns.Add(t, w);
        info.Text = "MAC/vendor detection only works on the local L2 network (not through routers or VPNs). " +
                    "Probe sends ping, TCP connect, one empty UDP datagram and HTTP GET / only - no PLC data.";
        Controls.AddRange(new Control[] { list, top, progress, info });

        external.CheckedChanged += (_, _) => { for (int i = 0; i < found.Count; i++) if (found[i].External) ifaces.SetItemChecked(i, external.Checked); };
        btnScan.Click += async (_, _) => await Scan();
        btnCancel.Click += (_, _) => cts?.Cancel();
        btnUse.Click += (_, _) => Use();
        list.DoubleClick += (_, _) => Use();
        list.SelectedIndexChanged += (_, _) => btnUse.Enabled = list.SelectedItems.Count > 0;
        FormClosing += (_, _) => cts?.Cancel();
        LoadInterfaces();
        Shown += async (_, _) => { if (!string.IsNullOrWhiteSpace(cfgHost)) await Scan(hostOnly: true); };
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
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                uint a = ToU(ua.Address), m = ToU(ua.IPv4Mask);
                if ((a >> 24) == 169 && ((a >> 16) & 255) == 254) continue; // APIPA
                bool cgnat = (a & 0xFFC00000) == 0x64400000; // 100.64.0.0/10 (Tailscale)
                var f = new Iface { Name = ni.Name, Desc = ni.Description, Index = p4?.Index ?? -1, Addr = a, Mask = m, External = ext || cgnat, Virtual = virt };
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
            if (!ifaces.GetItemChecked(i)) continue;
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
            else targets = BuildTargets();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        var ips = new Dictionary<uint, Target>();
        foreach (var t in targets) for (ulong a = t.First; a <= t.Last; a++) if (!ips.ContainsKey((uint)a)) ips[(uint)a] = t;
        foreach (var f in found) ips.Remove(f.Addr); // skip our own addresses
        if (ips.Count == 0) { info.Text = "Nothing to scan: tick an interface or enter a range."; return; }

        cts = new CancellationTokenSource(); var ct = cts.Token;
        btnScan.Enabled = false; btnCancel.Enabled = true; btnUse.Enabled = false;
        if (!hostOnly) list.Items.Clear();
        progress.Maximum = ips.Count; progress.Value = 0;
        bool doHttp = http.Checked;
        var results = new List<Result>();
        var gate = new SemaphoreSlim(Parallel);
        int done = 0;
        info.Text = $"Scanning {ips.Count} addresses...";
        var tasks = ips.Select(async kv =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var r = await ProbeHost(ToIp(kv.Key), kv.Value.External || hostOnly, doHttp, kv.Value.Source, hostOnly, ct).ConfigureAwait(false);
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
                if (BeijerOui.TryGetValue(mac.Substring(0, 8), out var v)) { r.Vendor = v; r.Panel = true; }
            }
        foreach (var r in results.OrderByDescending(r => r.Panel).ThenByDescending(r => r.Tcp == "open" || r.Udp == "open").ThenBy(r => ToU(IPAddress.Parse(r.Ip))))
        {
            string likely = r.Panel ? "YES (Beijer MAC)" : (r.Tcp == "open" || r.Udp == "open") ? $"maybe (:{cfgPort})" : "";
            var it = new ListViewItem(new[] { r.Ip, likely, r.Mac, r.Ms >= 0 ? r.Ms.ToString() : "-", r.Tcp, r.Udp, r.Http, r.Source }) { Tag = r };
            if (r.Panel) it.Font = new Font(list.Font, FontStyle.Bold);
            list.Items.Add(it);
        }
        info.Text = (ct.IsCancellationRequested ? "Cancelled. " : "Done. ") + $"{results.Count} responder(s) of {ips.Count} scanned. " +
                    "MAC/vendor detection only works on the local L2 network. Double-click or press Use to set the panel IP.";
        btnScan.Enabled = true; btnCancel.Enabled = false;
        cts.Dispose(); cts = null;
    }

    void BeginInvokeSafe(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch (InvalidOperationException) { } }

    async Task<Result> ProbeHost(IPAddress ip, bool slow, bool doHttp, string src, bool always, CancellationToken ct)
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

    static IEnumerable<uint> TailscalePeers()
    {
        var exe = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"), "tailscale.exe" }
            .FirstOrDefault(p => !Path.IsPathRooted(p) || File.Exists(p));
        string json;
        try
        {
            var psi = new ProcessStartInfo(exe, "status --json") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var pr = Process.Start(psi);
            var read = pr.StandardOutput.ReadToEndAsync();
            if (!pr.WaitForExit(4000)) { try { pr.Kill(); } catch { } return Array.Empty<uint>(); }
            json = read.Result;
        }
        catch { return Array.Empty<uint>(); }
        int peers = json.IndexOf("\"Peer\"", StringComparison.Ordinal);
        if (peers < 0) return Array.Empty<uint>();
        return Regex.Matches(json.Substring(peers), "\"(100\\.\\d+\\.\\d+\\.\\d+)\"").Cast<Match>()
            .Select(m => ToU(IPAddress.Parse(m.Groups[1].Value))).Distinct().Take(MaxHostsPerTarget).ToList();
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
