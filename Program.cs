using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BeijerFXBridge;

static class Program
{
    const string Usage =
        "Usage: BeijerFXBridgeNet.exe [--cli] [--com COM11] [--host 10.0.2.10] [--port 6004] [--protocol udp|tcp]\n" +
        "         [--baud 115200] [--parity E|N|O] [--bytesize 7|8] [--stopbits 1|2] [--bind 0.0.0.0] [--local-port 0] [-v]\n" +
        "Without --cli the settings window opens. Defaults come from config.ini next to the exe.";

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--cli")) return Cli(args);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    static int Cli(string[] args)
    {
        AttachConsole(-1); // WinExe: write to the parent console, if any
        var c = Config.Load();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i], k = a.TrimStart('-').Replace('-', '_');
            if (a is "-h" or "--help") { Console.WriteLine(Usage); return 0; }
            if (a is "-v" or "--verbose") c["verbose"] = "true";
            else if (a is "--cli" or "--gui") { }
            else if (a.StartsWith("--") && c.ContainsKey(k) && i + 1 < args.Length) c[k] = args[++i];
            else { Console.Error.WriteLine($"Unknown argument: {a}\n{Usage}"); return 2; }
        }
        Bridge b;
        try { b = new Bridge(c, Console.WriteLine); }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("Stopping..."); b.Stop(); };
        b.Start();
        b.Stopped.WaitOne();
        b.Dispose();
        return 0;
    }
}

static class Config
{
    static readonly string[] Keys = { "com", "plc_com", "host", "port", "protocol", "baud", "parity", "bytesize", "stopbits", "bind", "local_port", "verbose" };
    static readonly string[] Defaults = { "COM11", "COM10", "10.0.2.10", "6004", "udp", "115200", "E", "7", "1", "0.0.0.0", "0", "true" };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.ini");

    public static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < Keys.Length; i++) d[Keys[i]] = Defaults[i];
        if (!File.Exists(FilePath)) return d;
        var raw = new Dictionary<string, string>();
        bool inBridge = false;
        foreach (var l in File.ReadAllLines(FilePath))
        {
            var line = l.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line[0] == '[') { inBridge = line.Equals("[bridge]", StringComparison.OrdinalIgnoreCase); continue; }
            int eq = line.IndexOfAny(new[] { '=', ':' });
            if (inBridge && eq > 0) raw[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
        }
        if (!raw.ContainsKey("plc_com") && raw.TryGetValue("gx_com", out var gx)) raw["plc_com"] = gx; // old key name
        foreach (var k in Keys) if (raw.TryGetValue(k, out var v)) d[k] = v;
        d["protocol"] = d["protocol"].ToLowerInvariant() == "tcp" ? "tcp" : "udp";
        return d;
    }

    public static void Save(Dictionary<string, string> d)
    {
        var sb = new StringBuilder("[bridge]").AppendLine();
        foreach (var k in Keys) sb.AppendLine($"{k} = {d[k]}");
        File.WriteAllText(FilePath, sb.AppendLine().ToString());
    }
}

sealed class Bridge : IDisposable
{
    readonly Action<string> log;
    readonly bool verbose, tcpMode;
    readonly string banner;
    readonly IPEndPoint peer;
    readonly SerialPort ser;
    readonly UdpClient udp;
    volatile TcpClient tcp;
    readonly object reconnectLock = new();
    readonly CancellationTokenSource cts = new();

    public WaitHandle Stopped => cts.Token.WaitHandle;
    bool IsStopped => cts.IsCancellationRequested;

    public Bridge(Dictionary<string, string> c, Action<string> log)
    {
        this.log = log;
        verbose = c["verbose"].Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
        tcpMode = c["protocol"].Trim().ToLowerInvariant() == "tcp";
        double stop = double.Parse(c["stopbits"], CultureInfo.InvariantCulture);
        banner = $"Bridging {c["com"]} <-> {(tcpMode ? "TCP" : "UDP")} {c["host"]}:{c["port"]}  {c["baud"]} {c["bytesize"]}{c["parity"]}{(int)stop}";
        string host = c["host"].Trim();
        peer = new IPEndPoint(IPAddress.TryParse(host, out var ip) ? ip
            : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork), int.Parse(c["port"]));
        ser = new SerialPort(c["com"].Trim(), int.Parse(c["baud"]),
            c["parity"].Trim().ToUpperInvariant() switch { "N" => Parity.None, "O" => Parity.Odd, _ => Parity.Even },
            int.Parse(c["bytesize"]),
            stop == 2 ? StopBits.Two : stop == 1.5 ? StopBits.OnePointFive : StopBits.One)
        { ReadTimeout = 500, WriteTimeout = 2000 };
        var bind = new IPEndPoint(IPAddress.Parse(c["bind"]), int.Parse(c["local_port"]));
        try { ser.Open(); }
        catch (Exception e) { throw new Exception($"Cannot open {ser.PortName}: {e.Message}"); }
        try
        {
            if (tcpMode) tcp = TcpConnect();
            else udp = new UdpClient(bind); // random local port when local_port = 0
        }
        catch (Exception e) { ser.Close(); throw new Exception($"Cannot connect to {peer}: {e.Message}"); }
    }

    public void Start()
    {
        log(banner);
        if (tcpMode) log($"TCP connected to {peer}");
        new Thread(ComToNet) { IsBackground = true, Name = "com2net" }.Start();
        new Thread(NetToCom) { IsBackground = true, Name = "net2com" }.Start();
    }

    public void Stop() => cts.Cancel();

    public void Dispose()
    {
        cts.Cancel();
        try { ser.Close(); } catch { }
        try { udp?.Close(); } catch { }
        try { tcp?.Close(); } catch { }
    }

    void V(string msg) { if (verbose) log(msg); }

    TcpClient TcpConnect()
    {
        var t = new TcpClient(peer.AddressFamily) { NoDelay = true };
        try
        {
            if (!t.ConnectAsync(peer.Address, peer.Port).Wait(5000)) throw new TimeoutException("connect timed out");
            return t;
        }
        catch (AggregateException e) { t.Dispose(); throw e.InnerException; }
        catch { t.Dispose(); throw; }
    }

    // Called by either thread when the TCP link breaks; only the first caller reconnects.
    void Reconnect(TcpClient broken)
    {
        lock (reconnectLock)
        {
            if (tcp != broken) return;
            try { broken?.Dispose(); } catch { }
            tcp = null;
            while (!IsStopped)
            {
                try
                {
                    var t = TcpConnect();
                    if (IsStopped) { t.Dispose(); return; }
                    tcp = t;
                    log($"TCP connected to {peer}");
                    return;
                }
                catch (Exception e)
                {
                    log($"TCP connect failed: {e.Message}; retry in 2s");
                    Stopped.WaitOne(2000);
                }
            }
        }
    }

    void ComToNet()
    {
        var buf = new byte[4096];
        while (!IsStopped)
        {
            int n;
            try { n = ser.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch (Exception e) { if (!IsStopped) log($"Serial error: {e.Message}"); Stop(); return; }
            if (!tcpMode)
            {
                try { udp.Send(buf, n, peer); V($"COM->UDP {n} B to {peer}"); }
                catch (Exception e) { if (!IsStopped) log($"Net send error: {e.Message}"); }
                continue;
            }
            var t = tcp;
            if (t == null) continue; // reconnecting; data is dropped
            try { t.GetStream().Write(buf, 0, n); V($"COM->TCP {n} B to {peer}"); }
            catch (Exception e) { if (IsStopped) return; log($"TCP send error: {e.Message}; reconnecting"); Reconnect(t); }
        }
    }

    void NetToCom()
    {
        var buf = new byte[65535];
        while (!IsStopped)
        {
            byte[] data;
            int n;
            string from;
            try
            {
                if (tcpMode)
                {
                    var t = tcp;
                    if (t == null) { Thread.Sleep(100); continue; }
                    try { n = t.GetStream().Read(buf, 0, buf.Length); }
                    catch (Exception e) { if (IsStopped) return; log($"Net recv error: {e.Message}"); Reconnect(t); continue; }
                    if (n == 0) { if (IsStopped) return; log("TCP peer closed; reconnecting"); Reconnect(t); continue; }
                    data = buf;
                    from = peer.ToString();
                }
                else
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    data = udp.Receive(ref ep);
                    n = data.Length;
                    from = ep.ToString();
                    if (n == 0) continue;
                    if (!ep.Address.Equals(peer.Address)) { V($"ignore UDP from {ep}"); continue; }
                }
            }
            catch (Exception e) { if (IsStopped) return; log($"Net recv error: {e.Message}"); Thread.Sleep(100); continue; }
            try { ser.Write(data, 0, n); V($"{(tcpMode ? "TCP" : "UDP")}->COM {n} B from {from}"); }
            catch (Exception e) { if (!IsStopped) log($"Serial write error: {e.Message}"); Stop(); return; }
        }
    }
}
