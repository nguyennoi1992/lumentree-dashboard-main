using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LumenTreeInfo.Lib;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace LumenTreeInfo.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProxyController : ControllerBase
{
    private readonly ProxyManager _proxyManager;

    public ProxyController(ProxyManager proxyManager)
    {
        _proxyManager = proxyManager;
    }

    /// <summary>
    /// Lấy trạng thái danh sách proxy hiện hành, số lượng alive và proxy tối ưu.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var bestApi = _proxyManager.GetBestApiProxy();
        var bestMqtt = _proxyManager.GetBestMqttProxy();
        var allAlive = _proxyManager.AllAliveProxies;

        return Ok(new
        {
            success = true,
            isRefreshing = _proxyManager.IsRefreshing,
            lastRefreshed = _proxyManager.LastRefreshed == DateTime.MinValue ? null : _proxyManager.LastRefreshed.ToString("yyyy-MM-dd HH:mm:ss"),
            aliveCount = allAlive.Count,
            aliveApiCount = _proxyManager.AliveApiProxies.Count,
            aliveMqttCount = _proxyManager.AliveMqttProxies.Count,
            bestApiProxy = bestApi != null ? new
            {
                url = bestApi.ProxyUrl,
                latencyMs = bestApi.LatencyMs,
                protocol = bestApi.Protocol
            } : null,
            bestMqttProxy = bestMqtt != null ? new
            {
                url = bestMqtt.ProxyUrl,
                latencyMs = bestMqtt.LatencyMs,
                protocol = bestMqtt.Protocol
            } : null,
            proxies = allAlive.Take(30).Select(p => new
            {
                url = p.ProxyUrl,
                protocol = p.Protocol,
                canConnectApi = p.CanConnectApi,
                canConnectMqtt = p.CanConnectMqtt,
                latencyMs = p.LatencyMs,
                source = p.Source,
                lastChecked = p.LastChecked.ToString("HH:mm:ss")
            })
        });
    }

    /// <summary>
    /// Kích hoạt làm mới danh sách proxy và kiểm tra tính sẵn sàng ngay lập tức.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> TriggerRefresh(CancellationToken ct)
    {
        if (_proxyManager.IsRefreshing)
        {
            return Accepted(new
            {
                success = true,
                message = "Đang trong tiến trình làm mới danh sách proxy. Vui lòng kiểm tra lại sau giây lát.",
                isRefreshing = true
            });
        }

        Log.Information("Người dùng kích hoạt làm mới danh sách proxy qua API");
        var count = await _proxyManager.RefreshAndCheckAsync(120, ct);

        return Ok(new
        {
            success = true,
            message = $"Đã làm mới danh sách proxy. Tìm thấy {count} proxy đang hoạt động tốt.",
            aliveCount = count,
            bestApiProxy = _proxyManager.GetBestApiProxy()?.ProxyUrl,
            bestMqttProxy = _proxyManager.GetBestMqttProxy()?.ProxyUrl
        });
    }

    /// <summary>
    /// Kiểm tra tính sẵn sàng của một proxy cụ thể.
    /// </summary>
    [HttpGet("test")]
    public async Task<IActionResult> TestProxy([FromQuery] string host, [FromQuery] int port, [FromQuery] string protocol = "http", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
        {
            return BadRequest(new { error = "Tham số host và port không hợp lệ" });
        }

        if (IsPrivateOrLocalHost(host))
        {
            return BadRequest(new { error = "Không cho phép kiểm tra địa chỉ mạng nội bộ hoặc loopback (SSRF protection)" });
        }

        var entry = await _proxyManager.CheckProxyAvailabilityAsync(host, port, protocol, "manual_test", ct);
        return Ok(new
        {
            success = true,
            proxy = entry.ProxyUrl,
            canConnectApi = entry.CanConnectApi,
            canConnectMqtt = entry.CanConnectMqtt,
            latencyMs = entry.LatencyMs,
            isAlive = entry.IsAlive
        });
    }

    private static bool IsPrivateOrLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
        }
        return false;
    }
}
