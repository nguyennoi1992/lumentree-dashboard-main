using System;
using System.Threading;
using System.Threading.Tasks;
using LumenTreeInfo.Lib;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace LumenTreeInfo.API.Services;

/// <summary>
/// Background Service tự động chạy định kỳ làm mới danh sách proxy và kiểm tra tính sẵn sàng.
/// </summary>
public class ProxyBackgroundService : BackgroundService
{
    private readonly ProxyManager _proxyManager;
    private readonly TimeSpan _refreshInterval;

    public ProxyBackgroundService(ProxyManager proxyManager)
    {
        _proxyManager = proxyManager;

        // Đọc chu kỳ refresh từ biến môi trường (mặc định 15 phút, tối thiểu 3 phút)
        var intervalMinutesStr = Environment.GetEnvironmentVariable("PROXY_REFRESH_MINUTES");
        var minutes = 15;
        if (!string.IsNullOrEmpty(intervalMinutesStr) && int.TryParse(intervalMinutesStr, out var parsedMinutes))
        {
            minutes = Math.Max(3, parsedMinutes);
        }

        _refreshInterval = TimeSpan.FromMinutes(minutes);
        Log.Information("ProxyBackgroundService khởi tạo với chu kỳ làm mới: {Minutes} phút", minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Chờ 5 giây sau khi ứng dụng khởi động xong rồi mới bắt đầu kiểm tra đợt đầu
        try
        {
            await Task.Delay(5000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Log.Information("ProxyBackgroundService: Bắt đầu định kỳ refresh danh sách proxy...");
                var count = await _proxyManager.RefreshAndCheckAsync(120, stoppingToken);
                var bestApi = _proxyManager.GetBestApiProxy();
                var bestMqtt = _proxyManager.GetBestMqttProxy();

                Log.Information("Proxy pool định kỳ đã cập nhật: {Total} alive (API: {ApiProxy}, MQTT: {MqttProxy})",
                    count,
                    bestApi != null ? $"{bestApi.ProxyUrl} ({bestApi.LatencyMs}ms)" : "None",
                    bestMqtt != null ? $"{bestMqtt.ProxyUrl} ({bestMqtt.LatencyMs}ms)" : "None");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Lỗi khi chạy định kỳ ProxyBackgroundService: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(_refreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.Information("ProxyBackgroundService đã dừng.");
    }
}
