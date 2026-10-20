using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace LumenTreeInfo.Lib;

/// <summary>
/// Cầu nối TCP trung chuyển MQTT tới lesvr.suntcn.com:1886 thông qua Proxy tunnel
/// (HTTP CONNECT hoặc SOCKS5) để vượt qua tường lửa Alibaba Cloud.
/// </summary>
public class MqttProxyBridge : IDisposable
{
    private const string TargetHost = "lesvr.suntcn.com";
    private const int TargetPort = 1886;

    private readonly ProxyManager? _proxyManager;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly SemaphoreSlim _handshakeThrottler = new SemaphoreSlim(4, 4);
    private volatile ProxyEntry? _activeWorkingProxy;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;

    public MqttProxyBridge(ProxyManager? proxyManager = null)
    {
        _proxyManager = proxyManager;
    }

    public void Start(int preferredPort = 11886)
    {
        if (IsRunning) return;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, preferredPort);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            Log.Warning("Không thể lắng nghe trên cổng {Port}: {Msg}. Chuyển sang cổng ngẫu nhiên.", preferredPort, ex.Message);
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log.Information("MQTT Proxy Bridge đã khởi động trên 127.0.0.1:{Port} (Target: {Target}:{TargetPort})", Port, TargetHost, TargetPort);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log.Warning("MQTT Bridge accept error: {Message}", ex.Message);
                    await Task.Delay(500, ct);
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient localClient, CancellationToken ct)
    {
        using (localClient)
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            TcpClient? remoteClient = null;

            try
            {
                await _handshakeThrottler.WaitAsync(linkedCts.Token);
                try
                {
                    // 1. Thử proxy đã xác nhận hoạt động tốt trước đó
                    var knownProxy = _activeWorkingProxy;
                    if (knownProxy != null && knownProxy.CanConnectMqtt)
                    {
                        var fastClient = await ConnectViaProxyAsync(knownProxy, linkedCts.Token);
                        if (fastClient != null)
                        {
                            remoteClient = fastClient;
                        }
                        else
                        {
                            _activeWorkingProxy = null;
                        }
                    }

                    // 2. Nếu chưa có kết nối, thử các proxy ứng viên với trần timeout tổng 8 giây
                    if (remoteClient == null)
                    {
                        var mqttProxies = _proxyManager?.AliveMqttProxies;
                        if (mqttProxies != null)
                        {
                            using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
                            searchCts.CancelAfter(TimeSpan.FromSeconds(8));

                            foreach (var proxy in mqttProxies.Take(3))
                            {
                                if (searchCts.IsCancellationRequested) break;
                                if (proxy == knownProxy) continue;

                                Log.Information("MQTT Bridge: Định tuyến kết nối MQTT qua proxy {Proxy}", proxy.ProxyUrl);
                                var proxiedClient = await ConnectViaProxyAsync(proxy, searchCts.Token);
                                if (proxiedClient != null)
                                {
                                    remoteClient = proxiedClient;
                                    _activeWorkingProxy = proxy;
                                    break;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _handshakeThrottler.Release();
                }

                if (remoteClient == null)
                {
                    Log.Warning("MQTT Bridge: Không có proxy MQTT khả dụng, thử kết nối trực tiếp {Host}:{Port}...", TargetHost, TargetPort);
                    var directClient = new TcpClient();
                    try
                    {
                        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await directClient.ConnectAsync(TargetHost, TargetPort, connectCts.Token);
                        remoteClient = directClient;
                    }
                    catch (Exception ex)
                    {
                        directClient.Dispose();
                        Log.Warning("MQTT Bridge: Kết nối trực tiếp thất bại: {Message}", ex.Message);
                        try { localClient.Client?.Shutdown(SocketShutdown.Both); } catch { }
                        return;
                    }
                }

                using (remoteClient)
                using (var localStream = localClient.GetStream())
                using (var remoteStream = remoteClient.GetStream())
                {
                    var t1 = localStream.CopyToAsync(remoteStream, linkedCts.Token);
                    var t2 = remoteStream.CopyToAsync(localStream, linkedCts.Token);
                    var completed = await Task.WhenAny(t1, t2);

                    using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
                    drainCts.CancelAfter(TimeSpan.FromSeconds(2));

                    if (completed == t1)
                    {
                        try { remoteClient.Client?.Shutdown(SocketShutdown.Send); } catch { }
                        try { await Task.WhenAny(t2, Task.Delay(2000, drainCts.Token)); } catch (OperationCanceledException) { }
                    }
                    else
                    {
                        try { localClient.Client?.Shutdown(SocketShutdown.Send); } catch { }
                        try { await Task.WhenAny(t1, Task.Delay(2000, drainCts.Token)); } catch (OperationCanceledException) { }
                    }

                    if (!t1.IsCompleted || !t2.IsCompleted)
                    {
                        try { linkedCts.Cancel(); } catch { }
                    }
                    try
                    {
                        if (localClient.Client?.Connected == true)
                            localClient.Client.Shutdown(System.Net.Sockets.SocketShutdown.Both);
                    }
                    catch { /* ignore */ }
                    try
                    {
                        if (remoteClient.Client?.Connected == true)
                            remoteClient.Client.Shutdown(System.Net.Sockets.SocketShutdown.Both);
                    }
                    catch { /* ignore */ }
                    try
                    {
                        await Task.WhenAll(t1, t2);
                    }
                    catch (OperationCanceledException) { /* expected on linkedCts.Cancel() */ }
                    catch (Exception) { /* ignore expected stream closure */ }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected graceful cancellation
            }
            catch (Exception ex)
            {
                Log.Debug("MQTT Bridge connection closed: {Message}", ex.Message);
            }
        }
    }

    private static async Task<TcpClient?> ConnectViaProxyAsync(ProxyEntry proxy, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

            await client.ConnectAsync(proxy.Host, proxy.Port, timeoutCts.Token);
            var stream = client.GetStream();

            if (proxy.Protocol == "socks5")
            {
                // SOCKS5 handshake
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, timeoutCts.Token);
                var greetingResp = new byte[2];
                await ReadExactAsync(stream, greetingResp, timeoutCts.Token);
                if (greetingResp[0] != 0x05 || greetingResp[1] != 0x00)
                {
                    client.Dispose();
                    return null;
                }

                var domainBytes = Encoding.ASCII.GetBytes(TargetHost);
                var connectReq = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)domainBytes.Length };
                connectReq.AddRange(domainBytes);
                connectReq.Add((byte)((TargetPort >> 8) & 0xFF));
                connectReq.Add((byte)(TargetPort & 0xFF));

                await stream.WriteAsync(connectReq.ToArray(), timeoutCts.Token);

                // Đọc chính xác header reply 4 bytes (VER, REP, RSV, ATYP)
                var header = new byte[4];
                await ReadExactAsync(stream, header, timeoutCts.Token);
                if (header[0] != 0x05 || header[1] != 0x00)
                {
                    Log.Debug("SOCKS5 connect to {Host}:{Port} failed with reply code: {Reply}", TargetHost, TargetPort, header[1]);
                    client.Dispose();
                    return null;
                }

                // Xả sạch phần còn lại của frame (BND.ADDR + BND.PORT) theo RFC 1928
                int remainingBytes = header[3] switch
                {
                    0x01 => 4 + 2,  // IPv4: 4 bytes IP + 2 bytes port
                    0x04 => 16 + 2, // IPv6: 16 bytes IP + 2 bytes port
                    0x03 => -1,     // Domain: 1 byte len + N bytes + 2 bytes port
                    _ => -2
                };

                if (remainingBytes == -2)
                {
                    client.Dispose();
                    return null;
                }

                if (remainingBytes == -1)
                {
                    var lenBuf = new byte[1];
                    await ReadExactAsync(stream, lenBuf, timeoutCts.Token);
                    if (lenBuf[0] == 0)
                    {
                        client.Dispose();
                        return null;
                    }
                    remainingBytes = lenBuf[0] + 2;
                }

                var remaining = new byte[remainingBytes];
                await ReadExactAsync(stream, remaining, timeoutCts.Token);
                return client;
            }
            else
            {
                // HTTP CONNECT tunnel
                var req = Encoding.ASCII.GetBytes($"CONNECT {TargetHost}:{TargetPort} HTTP/1.1\r\nHost: {TargetHost}:{TargetPort}\r\n\r\n");
                await stream.WriteAsync(req, timeoutCts.Token);

                using var ms = new MemoryStream();
                var oneByte = new byte[1];
                var headerDone = false;
                int window = 0;

                while (!headerDone && ms.Length < 2048)
                {
                    var read = await stream.ReadAsync(oneByte.AsMemory(0, 1), timeoutCts.Token);
                    if (read <= 0) break;
                    ms.WriteByte(oneByte[0]);
                    window = (window << 8) | oneByte[0];
                    if (window == 0x0D0A0D0A) // \r\n\r\n
                    {
                        headerDone = true;
                        break;
                    }
                }

                if (!headerDone)
                {
                    client.Dispose();
                    return null;
                }

                var resp = Encoding.ASCII.GetString(ms.ToArray());
                var firstLine = resp.Split("\r\n", 2)[0].Trim();
                if (!firstLine.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                    !firstLine.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                {
                    client.Dispose();
                    return null;
                }

                return client;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("MQTT Bridge: Không thể thiết lập tunnel qua proxy {Proxy}: {Msg}", proxy.ProxyUrl, ex.Message);
            client.Dispose();
            return null;
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read <= 0) throw new EndOfStreamException("Stream đóng sớm trước khi nhận đủ gói tin");
            totalRead += read;
        }
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
        }
        catch (Exception) { }

        try { _listener?.Stop(); } catch { }
        _listener = null;

        try { _acceptTask?.Wait(1000); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}
