using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace LumenTreeInfo.Lib;

/// <summary>
/// Đại diện cho một proxy trong danh sách và trạng thái kiểm tra tính sẵn sàng.
/// </summary>
public class ProxyEntry
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Protocol { get; set; } = "http"; // "http", "socks5"
    public bool CanConnectApi { get; set; }
    public bool CanConnectMqtt { get; set; }
    public long LatencyMs { get; set; }
    public long ApiLatencyMs { get; set; }
    public long MqttLatencyMs { get; set; }
    public DateTime LastChecked { get; set; }
    public string? Source { get; set; }

    public string ProxyUrl => $"{Protocol}://{Host}:{Port}";

    public bool IsAlive => CanConnectApi || CanConnectMqtt;
}

/// <summary>
/// Quản lý danh sách proxy miễn phí, tự động tải từ nguồn công khai,
/// kiểm tra tính sẵn sàng (Health Check) tới Lumentree API và MQTT Broker,
/// và hỗ trợ xoay vòng / chọn proxy tối ưu.
/// </summary>
public class ProxyManager
{
    private static readonly string[] DefaultFreeSources =
    [
        "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt",
        "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
        "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks5.txt",
        "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt"
    ];

    private readonly List<ProxyEntry> _allProxies = [];
    private readonly object _lock = new();

    public int TotalChecked => _allProxies.Count;
    public DateTime LastRefreshed { get; private set; } = DateTime.MinValue;
    public bool IsRefreshing { get; private set; }

    public IReadOnlyList<ProxyEntry> AliveApiProxies
    {
        get
        {
            lock (_lock)
            {
                return _allProxies.Where(p => p.CanConnectApi)
                                  .OrderBy(p => p.ApiLatencyMs)
                                  .ToList();
            }
        }
    }

    public IReadOnlyList<ProxyEntry> AliveMqttProxies
    {
        get
        {
            lock (_lock)
            {
                return _allProxies.Where(p => p.CanConnectMqtt)
                                  .OrderBy(p => p.MqttLatencyMs)
                                  .ToList();
            }
        }
    }

    public IReadOnlyList<ProxyEntry> AllAliveProxies
    {
        get
        {
            lock (_lock)
            {
                return _allProxies.Where(p => p.IsAlive).OrderBy(p => p.LatencyMs).ToList();
            }
        }
    }

    /// <summary>
    /// Lấy proxy tốt nhất (latency thấp nhất) cho API
    /// </summary>
    public ProxyEntry? GetBestApiProxy() => AliveApiProxies.FirstOrDefault();

    /// <summary>
    /// Lấy proxy tốt nhất (latency thấp nhất) cho MQTT
    /// </summary>
    public ProxyEntry? GetBestMqttProxy() => AliveMqttProxies.FirstOrDefault();

    /// <summary>
    /// Làm mới danh sách proxy và kiểm tra tính sẵn sàng tới các đích Lumentree.
    /// </summary>
    public async Task<int> RefreshAndCheckAsync(int maxCandidates = 120, CancellationToken ct = default)
    {
        if (IsRefreshing)
        {
            Log.Warning("Proxy refresh is already running.");
            return AllAliveProxies.Count;
        }

        IsRefreshing = true;
        var sw = Stopwatch.StartNew();

        try
        {
            Log.Information("Bắt đầu thu thập và kiểm tra proxy miễn phí cho Lumentree...");
            var rawCandidates = await FetchRawProxiesAsync(maxCandidates, ct);
            Log.Information("Đã lấy được {Count} ứng viên proxy. Bắt đầu kiểm tra kết nối...", rawCandidates.Count);

            var tested = new ConcurrentBag<ProxyEntry>();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 24,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(rawCandidates, parallelOptions, async (candidate, token) =>
            {
                var entry = await CheckProxyAvailabilityAsync(candidate.host, candidate.port, candidate.protocol, candidate.source, token);
                if (entry.IsAlive)
                {
                    tested.Add(entry);
                }
            });

            lock (_lock)
            {
                _allProxies.Clear();
                _allProxies.AddRange(tested.OrderBy(p => p.LatencyMs));
                LastRefreshed = DateTime.Now;
            }

            sw.Stop();
            Log.Information("Hoàn tất kiểm tra proxy trong {Elapsed:0.0}s. Tìm thấy: {ApiCount} API, {MqttCount} MQTT alive.",
                sw.Elapsed.TotalSeconds, AliveApiProxies.Count, AliveMqttProxies.Count);

            return AllAliveProxies.Count;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Lỗi trong quá trình refresh proxy: {Message}", ex.Message);
            return AllAliveProxies.Count;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Kiểm tra tính sẵn sàng của 1 proxy cụ thể tới Lumentree API và MQTT Broker.
    /// </summary>
    public async Task<ProxyEntry> CheckProxyAvailabilityAsync(string host, int port, string protocol, string? source = null, CancellationToken ct = default)
    {
        var entry = new ProxyEntry
        {
            Host = host,
            Port = port,
            Protocol = protocol.ToLowerInvariant(),
            Source = source,
            LastChecked = DateTime.Now
        };

        if (entry.Protocol == "http")
        {
            var mqttSw = Stopwatch.StartNew();
            if (await TestHttpConnectTunnelAsync(host, port, "lesvr.suntcn.com", 1886, 2500, ct))
            {
                entry.CanConnectMqtt = true;
                entry.MqttLatencyMs = mqttSw.ElapsedMilliseconds;
                entry.LatencyMs = entry.MqttLatencyMs;
            }
            else
            {
                mqttSw.Restart();
                if (await TestHttpConnectTunnelAsync(host, port, "lesvr.suntcn.com", 8083, 2500, ct))
                {
                    entry.CanConnectMqtt = true;
                    entry.MqttLatencyMs = mqttSw.ElapsedMilliseconds;
                    entry.LatencyMs = entry.MqttLatencyMs;
                }
            }

            var apiSw = Stopwatch.StartNew();
            entry.CanConnectApi = await TestHttpApiAsync(host, port, 1500, ct);
            if (entry.CanConnectApi)
            {
                entry.ApiLatencyMs = apiSw.ElapsedMilliseconds;
                entry.LatencyMs = entry.CanConnectMqtt ? Math.Min(entry.ApiLatencyMs, entry.MqttLatencyMs) : entry.ApiLatencyMs;
            }
        }
        else if (entry.Protocol == "socks5")
        {
            var mqttSw = Stopwatch.StartNew();
            if (await TestSocks5TcpAsync(host, port, "lesvr.suntcn.com", 1886, 2500, ct))
            {
                entry.CanConnectMqtt = true;
                entry.MqttLatencyMs = mqttSw.ElapsedMilliseconds;
                entry.LatencyMs = entry.MqttLatencyMs;
            }
            else
            {
                mqttSw.Restart();
                if (await TestSocks5TcpAsync(host, port, "lesvr.suntcn.com", 8083, 2500, ct))
                {
                    entry.CanConnectMqtt = true;
                    entry.MqttLatencyMs = mqttSw.ElapsedMilliseconds;
                    entry.LatencyMs = entry.MqttLatencyMs;
                }
            }

            var apiSw = Stopwatch.StartNew();
            entry.CanConnectApi = await TestSocks5TcpAsync(host, port, "lesvr.suntcn.com", 80, 1500, ct);
            if (entry.CanConnectApi)
            {
                entry.ApiLatencyMs = apiSw.ElapsedMilliseconds;
                entry.LatencyMs = entry.CanConnectMqtt ? Math.Min(entry.ApiLatencyMs, entry.MqttLatencyMs) : entry.ApiLatencyMs;
            }
        }
        else
        {
            return entry;
        }

        return entry;
    }

    private static async Task<bool> TestHttpApiAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(host, port, timeoutCts.Token);
            var stream = client.GetStream();

            var req = Encoding.ASCII.GetBytes("GET http://lesvr.suntcn.com/ HTTP/1.1\r\nHost: lesvr.suntcn.com\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req, timeoutCts.Token);

            var buffer = new byte[256];
            var bytesRead = await stream.ReadAsync(buffer, timeoutCts.Token);
            if (bytesRead > 0)
            {
                var resp = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                return resp.StartsWith("HTTP/1.");
            }
        }
        catch
        {
            // Ignore connection errors
        }
        return false;
    }

    private static async Task<bool> TestHttpConnectTunnelAsync(string host, int port, string targetHost, int targetPort, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(host, port, timeoutCts.Token);
            var stream = client.GetStream();

            var req = Encoding.ASCII.GetBytes($"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n");
            await stream.WriteAsync(req, timeoutCts.Token);

            var buffer = new byte[256];
            var bytesRead = await stream.ReadAsync(buffer, timeoutCts.Token);
            if (bytesRead > 0)
            {
                var resp = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                return resp.Contains("200 Connection") || resp.Contains(" 200 ");
            }
        }
        catch
        {
            // Ignore connection errors
        }
        return false;
    }

    private static async Task<bool> TestSocks5TcpAsync(string host, int port, string targetHost, int targetPort, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            await client.ConnectAsync(host, port, timeoutCts.Token);
            var stream = client.GetStream();

            // 1. SOCKS5 Greeting: VER=5, NMETHODS=1, METHOD=0
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, timeoutCts.Token);
            var greetingResp = new byte[2];
            var r1 = await stream.ReadAsync(greetingResp, timeoutCts.Token);
            if (r1 < 2 || greetingResp[0] != 0x05 || greetingResp[1] != 0x00)
            {
                return false;
            }

            // 2. CONNECT request to target domain + port
            var domainBytes = Encoding.ASCII.GetBytes(targetHost);
            var connectReq = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)domainBytes.Length };
            connectReq.AddRange(domainBytes);
            connectReq.Add((byte)((targetPort >> 8) & 0xFF));
            connectReq.Add((byte)(targetPort & 0xFF));

            await stream.WriteAsync(connectReq.ToArray(), timeoutCts.Token);
            var connectResp = new byte[16];
            var r2 = await stream.ReadAsync(connectResp, timeoutCts.Token);
            return r2 >= 2 && connectResp[0] == 0x05 && connectResp[1] == 0x00;
        }
        catch
        {
            // Ignore connection errors
        }
        return false;
    }

    private static async Task<List<(string host, int port, string protocol, string source)>> FetchRawProxiesAsync(int maxCandidates, CancellationToken ct)
    {
        var result = new List<(string host, int port, string protocol, string source)>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (LumentreeProxyCollector)");

        // 1. Đọc file proxies.txt cục bộ nếu tồn tại (seed proxies)
        var localCandidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "proxies.txt"),
            Path.Combine(Directory.GetCurrentDirectory(), "proxies.txt"),
            "/u01/workspace/ubuntu-init/tools/lumentree-dashboard/proxies.txt"
        };

        foreach (var localFile in localCandidatePaths)
        {
            if (File.Exists(localFile))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(localFile, ct);
                    foreach (var line in lines)
                    {
                        if (TryParseProxyLine(line, "http", "local_seed", out var parsed))
                        {
                            if (!result.Any(r => r.host == parsed.host && r.port == parsed.port))
                            {
                                result.Add(parsed);
                            }
                        }
                    }
                    Log.Information("Đã nạp {Count} seed proxies từ file {File}", result.Count, localFile);
                }
                catch (Exception ex)
                {
                    Log.Warning("Không thể đọc file proxies.txt cục bộ: {Message}", ex.Message);
                }
                break;
            }
        }

        // 2. Tải từ các nguồn trực tuyến công khai
        foreach (var url in DefaultFreeSources)
        {
            if (result.Count >= maxCandidates * 2) break;

            try
            {
                var defaultProtocol = url.Contains("socks5") ? "socks5" : "http";
                var body = await http.GetStringAsync(url, ct);
                var lines = body.Split('\n');
                var count = 0;

                foreach (var line in lines)
                {
                    if (TryParseProxyLine(line, defaultProtocol, "public_list", out var parsed))
                    {
                        result.Add(parsed);
                        count++;
                        if (count >= 35) break; // Lấy 35 ứng viên từ mỗi nguồn để đa dạng hóa
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Không thể tải proxy từ {Url}: {Message}", url, ex.Message);
            }
        }

        // Shuffle nhẹ để kiểm tra đa dạng
        var rng = new Random();
        return result.OrderBy(_ => rng.Next()).Take(maxCandidates).ToList();
    }

    private static bool TryParseProxyLine(string line, string defaultProtocol, string source, out (string host, int port, string protocol, string source) parsed)
    {
        parsed = default;
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) return false;

        var protocol = defaultProtocol;
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            protocol = "http";
            trimmed = trimmed.Substring(7);
        }
        else if (trimmed.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            protocol = "socks5";
            trimmed = trimmed.Substring(9);
        }

        var parts = trimmed.Split(':');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var port) && port > 0 && port <= 65535)
        {
            parsed = (parts[0].Trim(), port, protocol, source);
            return true;
        }

        return false;
    }
}
