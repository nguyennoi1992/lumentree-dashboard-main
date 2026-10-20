using System.Diagnostics;
using LumenTreeInfo.API.Models;
using LumenTreeInfo.Lib;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace LumenTreeInfo.API.Controllers;

/// <summary>
/// Controller for handling home page and device data requests
/// </summary>
public class HomeController : Controller
{
    private readonly LumentreeClient _client;
    private readonly SolarMonitorService _solarMonitor;

    /// <summary>
    /// Initializes a new instance of the HomeController
    /// </summary>
    /// <param name="client">Lumentree API client</param>
    /// <param name="solarMonitor">Solar monitor service for MQTT data</param>
    public HomeController(LumentreeClient client, SolarMonitorService solarMonitor)
    {
        _client = client;
        _solarMonitor = solarMonitor;
    }

    private static DateTime GetVietnamNow()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Ho_Chi_Minh");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            return DateTime.UtcNow.AddHours(7);
        }
    }

    /// <summary>
    /// Returns the home page view
    /// </summary>
    [Route("/")]
    public IActionResult Index()
    {
        Log.Information("Rendering home page");
        return View();
    }

    /// <summary>
    /// Returns the calculator page
    /// </summary>
    [Route("/calculator")]
    public IActionResult Calculator()
    {
        Log.Information("Rendering calculator page");
        return PhysicalFile(
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "calculator.html"),
            "text/html"
        );
    }

    /// <summary>
    /// Gets and returns device information and energy data
    /// </summary>
    /// <param name="deviceId">The device ID to get information for</param>
    /// <param name="date">Optional date parameter (defaults to current date)</param>
    [Route("/device/{deviceId}")]
    public async Task<IActionResult> GetDeviceInfo(string deviceId, string? date)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            Log.Warning("Device ID is null or empty");
            return BadRequest(new { error = "Device ID is required", code = "MISSING_DEVICE_ID" });
        }

        Log.Debug("Getting device info for device {DeviceId} with date {Date}", deviceId, date);

        try
        {
            // Parse the date or use current date if not provided
            var queryDate = GetVietnamNow();
            if (!string.IsNullOrEmpty(date))
            {
                if (DateTime.TryParse(date, out var parsedDate))
                {
                    queryDate = parsedDate;
                    Log.Debug("Using parsed date: {QueryDate:yyyy-MM-dd}", queryDate);
                }
                else
                {
                    Log.Warning("Failed to parse date: {Date}, using current date instead", date);
                }
            }

            // Kích hoạt theo dõi thiết bị ngầm qua MQTT/Modbus
            _ = _solarMonitor.AddDevice(deviceId);

            // OPTION 0: Try LEHT API first (lehtapi.suntcn.com) if available
            var lehtResult = await TryLehtApiFallback(deviceId, queryDate);
            if (lehtResult != null)
            {
                Log.Information("Got data from LEHT API for device {DeviceId}", deviceId);
                return Json(lehtResult);
            }

            // OPTION 1: Query direct Suntcn API (lesvr.suntcn.com - tốc độ cao <100ms)
            // Cung cấp số liệu tổng sản lượng hôm nay (kWh) và biểu đồ 288 điểm đo thời gian thực
            Log.Debug("Querying direct API (lesvr.suntcn.com) for device {DeviceId}", deviceId);
            var (deviceInfo, pvData, batData, essentialLoad, grid, load) =
                await _client.GetAllDeviceDataAsync(deviceId, queryDate);

            if (deviceInfo != null)
            {
                // Check if we have high-frequency MQTT cached data to overlay real-time values
                var mqttData = _solarMonitor.GetCachedData(deviceId);

                object? realtime = null;
                DateTime now = GetVietnamNow();
                bool isToday = queryDate.Date == now.Date;

                if (isToday)
                {
                    int currentMinuteOfDay = Math.Max(0, now.Hour * 60 + now.Minute);
                    int targetIdxMax = Math.Clamp(currentMinuteOfDay / 5, 0, 287);
                    int GetLatestVal(List<int>? list)
                    {
                        if (list == null || list.Count == 0) return 0;
                        int maxIdx = Math.Min(list.Count - 1, targetIdxMax);
                        if (maxIdx < 0) return 0;
                        for (int i = maxIdx; i >= Math.Max(0, maxIdx - 3); i--)
                        {
                            if (list[i] != 0) return list[i];
                        }
                        return list[maxIdx];
                    }

                    int pvWatts = mqttData?.PvTotalPower ?? GetLatestVal(pvData?.TableValueInfo);
                    int gridWatts = mqttData?.GridValue ?? GetLatestVal(grid?.TableValueInfo);
                    int batWatts = mqttData?.BatteryValue ?? GetLatestVal(batData?.TableValueInfo);
                    int loadWatts = mqttData?.LoadValue ?? GetLatestVal(load?.TableValueInfo);
                    int essWatts = mqttData?.EssentialValue ?? GetLatestVal(essentialLoad?.TableValueInfo);

                    double batVoltage = mqttData?.BatteryVoltage ?? 0;
                    int batPercent = mqttData?.BatteryPercent ?? 0;
                    if (batPercent <= 0 && batVoltage > 40)
                    {
                        batPercent = CalculateLiFePO4Soc(batVoltage);
                    }
                    if (batPercent <= 0 && batData?.TableValueInfo != null && batData.TableValueInfo.Count > 0)
                    {
                        double netEnergyWh = 0;
                        for (int i = 0; i <= targetIdxMax && i < batData.TableValueInfo.Count; i++)
                        {
                            netEnergyWh += (-batData.TableValueInfo[i]) * (5.0 / 60.0);
                        }
                        batPercent = (int)Math.Clamp(25.0 + (netEnergyWh / 5000.0) * 100.0, 15.0, 100.0);
                    }

                    realtime = new
                    {
                        device_id = deviceId,
                        data = new
                        {
                            batterySoc = batPercent,
                            batteryVoltage = batVoltage,
                            batteryPower = Math.Abs(batWatts),
                            batteryStatus = batWatts > 0 ? "Discharging" : (batWatts < 0 ? "Charging" : "Idle"),
                            gridPowerFlow = gridWatts,
                            gridStatus = gridWatts > 0 ? "Importing" : (gridWatts < 0 ? "Exporting" : "Idle"),
                            homeLoad = loadWatts,
                            totalPvPower = pvWatts,
                            pv1Power = mqttData?.Pv1Power ?? pvWatts,
                            pv2Power = mqttData?.Pv2Power ?? 0,
                            temperature = mqttData?.DeviceTempValue ?? 0,
                            acOutputPower = essWatts,
                            acInputVoltage = mqttData?.GridVoltageValue ?? 220
                        }
                    };
                }

                var result = new
                {
                    DeviceInfo = deviceInfo,
                    Pv = pvData ?? CreateDefaultPvInfo(),
                    Bat = batData ?? CreateDefaultBatData(),
                    EssentialLoad = essentialLoad ?? CreateDefaultLoadInfo("EssentialLoad"),
                    Grid = grid ?? CreateDefaultLoadInfo("Grid"),
                    Load = load ?? CreateDefaultLoadInfo("HomeLoad"),
                    RealtimeData = realtime,
                    DataSource = "lesvr.suntcn.com"
                };
                return Json(result);
            }

            // OPTION 2: If primary REST API is unreachable, fall back to MQTT cached data
            var fallbackMqttData = _solarMonitor.GetCachedData(deviceId);
            if (fallbackMqttData != null)
            {
                Log.Debug("Using MQTT fallback data for device {DeviceId}", deviceId);
                return Json(BuildResponseFromMqttData(deviceId, fallbackMqttData));
            }
            
            // OPTION 4: Subscribe to device and wait for MQTT data
            Log.Information("No data available, subscribing to device {DeviceId} via MQTT and waiting for data...", deviceId);
            await _solarMonitor.AddDevice(deviceId);
            
            // Wait longer for MQTT data (increased from 2s to 6s for better reliability)
            // Try checking data every 1 second, up to 6 times
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                await Task.Delay(1000);
                var waitMqttData = _solarMonitor.GetCachedData(deviceId);
                if (waitMqttData != null)
                {
                    Log.Information("Got MQTT data after {Attempt}s for device {DeviceId}", attempt, deviceId);
                    return Json(BuildResponseFromMqttData(deviceId, waitMqttData));
                }
                Log.Debug("Waiting for MQTT data... attempt {Attempt}/6", attempt);
            }

            // All options failed
            Log.Warning("All data sources failed for device {DeviceId} after 6 seconds", deviceId);
            return NotFound(new { 
                error = $"Không tìm thấy thiết bị \"{deviceId}\". Thiết bị có thể offline hoặc Device ID không đúng.",
                code = "DEVICE_NOT_FOUND",
                deviceId = deviceId,
                suggestions = new[] {
                    "Kiểm tra lại Device ID - ID thường bắt đầu bằng P, H hoặc các ký tự khác theo loại thiết bị",
                    "Đảm bảo thiết bị đang online và kết nối internet",
                    "Thử refresh trang sau vài giây - lần đầu tiên kết nối có thể cần thêm thời gian",
                    "Kiểm tra xem thiết bị có hiển thị trên app Lumentree không",
                    $"Thử test connectivity tại: /debug/connectivity?deviceId={deviceId}"
                },
                help = "Nếu bạn mới mua thiết bị, vui lòng đợi 5-10 phút để hệ thống đồng bộ dữ liệu. Với thiết bị đã kích hoạt, hãy thử refresh lại trang sau vài giây.",
                apiVersion = "3.2",
                mqttStatus = "subscribed_waiting_for_data"
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while getting device data for {DeviceId}", deviceId);
            return StatusCode(500, new { 
                error = "Đã xảy ra lỗi khi xử lý yêu cầu. Vui lòng thử lại sau.",
                code = "INTERNAL_ERROR",
                details = ex.Message
            });
        }
    }
    
    /// <summary>
    /// Build API response from MQTT cached data
    /// </summary>
    private object BuildResponseFromMqttData(string deviceId, DeviceRealTimeData mqttData)
    {
        var emptyChartData = new List<int>(new int[288]);
        
        return new {
            DeviceInfo = new {
                DeviceId = deviceId,
                DeviceType = "Lumentree Inverter",
                OnlineStatus = 1,
                RemarkName = "",
                ErrorStatus = (string?)null
            },
            Pv = new {
                TableKey = "pv",
                TableName = "PV发电量",
                TableValue = 0,
                TableValueInfo = emptyChartData
            },
            Bat = new {
                Bats = new[] {
                    new { TableName = "电池充电电量", TableValue = 0, TableKey = "bat" },
                    new { TableName = "电池放电电量", TableValue = 0, TableKey = "batF" }
                },
                TableValueInfo = emptyChartData
            },
            EssentialLoad = new {
                TableKey = "essentialLoad",
                TableName = "不断电负载耗电量",
                TableValue = 0,
                TableValueInfo = emptyChartData
            },
            Grid = new {
                TableKey = "grid",
                TableName = "电网输入电量",
                TableValue = 0,
                TableValueInfo = emptyChartData
            },
            Load = new {
                TableKey = "homeload",
                TableName = "家庭负载耗电量",
                TableValue = 0,
                TableValueInfo = emptyChartData
            },
            RealtimeData = new {
                device_id = deviceId,
                data = new {
                    batterySoc = mqttData.BatteryPercent,
                    batteryVoltage = mqttData.BatteryVoltage ?? 0,
                    batteryPower = mqttData.BatteryValue,
                    batteryStatus = mqttData.BatteryStatus ?? (mqttData.BatteryValue > 0 ? "Discharging" : "Charging"),
                    gridPowerFlow = mqttData.GridValue,
                    gridStatus = mqttData.GridValue > 0 ? "Importing" : "Exporting",
                    homeLoad = mqttData.LoadValue,
                    totalPvPower = mqttData.PvTotalPower,
                    pv1Power = mqttData.Pv1Power,
                    pv2Power = mqttData.Pv2Power ?? 0,
                    temperature = mqttData.DeviceTempValue,
                    acOutputPower = mqttData.EssentialValue,
                    acInputVoltage = mqttData.GridVoltageValue
                },
                cells = new {
                    averageVoltage = 0,
                    cellVoltages = new Dictionary<string, double>(),
                    numberOfCells = 0
                }
            },
            DataSource = "mqtt",
            Timestamp = mqttData.Timestamp
        };
    }

    private static int CalculateLiFePO4Soc(double voltage)
    {
        if (voltage <= 0) return 0;
        if (voltage >= 54.0) return 100;
        if (voltage >= 53.6) return 95;
        if (voltage >= 53.2) return 90;
        if (voltage >= 52.8) return 80;
        if (voltage >= 52.5) return 70;
        if (voltage >= 52.1) return 60;
        if (voltage >= 51.6) return 50;
        if (voltage >= 51.2) return (int)(40 + (voltage - 51.2) / 0.4 * 10);
        if (voltage >= 50.8) return (int)(30 + (voltage - 50.8) / 0.4 * 10);
        if (voltage >= 50.0) return (int)(20 + (voltage - 50.0) / 0.8 * 10);
        if (voltage >= 48.0) return (int)(10 + (voltage - 48.0) / 2.0 * 10);
        if (voltage >= 46.0) return (int)(5 + (voltage - 46.0) / 2.0 * 5);
        return 0;
    }
    
    private static (string user, string pass) GetLehtCredentials()
    {
        var user = Environment.GetEnvironmentVariable("LEHT_USERNAME") ?? "Kim Lan 1";
        var pass = Environment.GetEnvironmentVariable("LEHT_PASSWORD") ?? "0986551480";
        return (user, pass);
    }

    /// <summary>
    /// Fallback to LEHT API (lehtapi.suntcn.com) - PRIMARY DATA SOURCE
    /// </summary>
    private async Task<object?> TryLehtApiFallback(string deviceId, DateTime queryDate)
    {
        try
        {
            var lehtClient = new LehtApiClient();
            var (lehtUser, lehtPass) = GetLehtCredentials();
            var loggedIn = await lehtClient.LoginAsync(lehtUser, lehtPass);
            
            if (!loggedIn)
            {
                Log.Warning("LEHT API login failed");
                return null;
            }
            
            var dayStr = queryDate.ToString("yyyy-MM-dd");
            var dayData = await lehtClient.GetAllDayDataAsync(deviceId, dayStr);
            
            if (dayData == null)
            {
                Log.Warning("LEHT API returned no data for device {DeviceId}", deviceId);
                return null;
            }
            
            // Get Battery SOC data separately (it's in a different endpoint)
            var batSocData = await lehtClient.GetBatSocAsync(deviceId, dayStr);
            
            // Build response compatible with frontend
            var deviceInfo = new {
                DeviceId = deviceId,
                DeviceType = "Lumentree Inverter (LEHT)",
                OnlineStatus = 1,
                RemarkName = "",
                ErrorStatus = (string?)null
            };
            
            // Convert tableValueInfo from double to int
            var pvValueInfo = dayData.Pv?.TableValueInfo?.Select(v => (int)v).ToList() ?? new List<int>();
            var batValueInfo = dayData.Bat?.TableValueInfo?.Select(v => (int)v).ToList() ?? new List<int>();
            var gridValueInfo = dayData.Grid?.TableValueInfo?.Select(v => (int)v).ToList() ?? new List<int>();
            var homeloadValueInfo = dayData.Homeload?.TableValueInfo?.Select(v => (int)v).ToList() ?? new List<int>();
            var essentialLoadValueInfo = dayData.EssentialLoad?.TableValueInfo?.Select(v => (int)v).ToList() ?? new List<int>();
            var batSocValueInfo = batSocData?.BatSoc?.TableValueInfo ?? new List<int>();
            
            return new {
                DeviceInfo = deviceInfo,
                Pv = new {
                    TableKey = dayData.Pv?.TableKey ?? "pv",
                    TableName = dayData.Pv?.TableName ?? "PV发电量",
                    TableValue = (int)(dayData.Pv?.TableValue ?? 0),
                    TableValueInfo = pvValueInfo
                },
                Bat = new {
                    Bats = new[] {
                        new { TableName = dayData.Bat?.TableName ?? "电池充电电量", TableValue = (int)(dayData.Bat?.TableValue ?? 0), TableKey = "bat" },
                        new { TableName = "电池放电电量", TableValue = 0, TableKey = "batF" }
                    },
                    TableValueInfo = batValueInfo
                },
                EssentialLoad = new {
                    TableKey = dayData.EssentialLoad?.TableKey ?? "essentialLoad",
                    TableName = dayData.EssentialLoad?.TableName ?? "不断电负载耗电量",
                    TableValue = (int)(dayData.EssentialLoad?.TableValue ?? 0),
                    TableValueInfo = essentialLoadValueInfo
                },
                Grid = new {
                    TableKey = dayData.Grid?.TableKey ?? "grid",
                    TableName = dayData.Grid?.TableName ?? "电网输入电量",
                    TableValue = (int)(dayData.Grid?.TableValue ?? 0),
                    TableValueInfo = gridValueInfo
                },
                Load = new {
                    TableKey = dayData.Homeload?.TableKey ?? "homeload",
                    TableName = dayData.Homeload?.TableName ?? "家庭负载耗电量",
                    TableValue = (int)(dayData.Homeload?.TableValue ?? 0),
                    TableValueInfo = homeloadValueInfo
                },
                BatSoc = new {
                    TableKey = batSocData?.BatSoc?.TableKey ?? "batSoc",
                    TableName = batSocData?.BatSoc?.TableName ?? "电池余量百分比",
                    TableValue = batSocValueInfo.Count > 0 ? batSocValueInfo.Last() : 0,
                    TableValueInfo = batSocValueInfo
                },
                DataSource = "lehtapi.suntcn.com",
                QueryDate = dayStr
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in LEHT API fallback for device {DeviceId}", deviceId);
            return null;
        }
    }
    
    /// <summary>
    /// Creates a default PV info object for cases when data is not available
    /// </summary>
    private static LumenTreeInfo.Lib.Models.LumentreeApiModels.PVInfo CreateDefaultPvInfo()
    {
        return new LumenTreeInfo.Lib.Models.LumentreeApiModels.PVInfo
        {
            TableKey = "pv",
            TableName = "PV",
            TableValue = 0,
            TableValueInfo = new List<int>()
        };
    }

    /// <summary>
    /// Creates a default battery data object for cases when data is not available
    /// </summary>
    private static LumenTreeInfo.Lib.Models.LumentreeApiModels.BatData CreateDefaultBatData()
    {
        return new LumenTreeInfo.Lib.Models.LumentreeApiModels.BatData
        {
            Bats = new List<LumenTreeInfo.Lib.Models.LumentreeApiModels.BatInfo>
            {
                new LumenTreeInfo.Lib.Models.LumentreeApiModels.BatInfo { TableName = "Charge", TableKey = "charge", TableValue = 0 },
                new LumenTreeInfo.Lib.Models.LumentreeApiModels.BatInfo { TableName = "Discharge", TableKey = "discharge", TableValue = 0 }
            },
            TableValueInfo = new List<int>()
        };
    }

    /// <summary>
    /// Creates a default load info object for cases when data is not available
    /// </summary>
    private static LumenTreeInfo.Lib.Models.LumentreeApiModels.LoadInfo CreateDefaultLoadInfo(string name)
    {
        return new LumenTreeInfo.Lib.Models.LumentreeApiModels.LoadInfo
        {
            TableKey = name.ToLower(),
            TableName = name,
            TableValue = 0,
            TableValueInfo = new List<int>()
        };
    }

    /// <summary>
    /// Gets today's energy summary for a device
    /// </summary>
    /// <param name="deviceId">The device ID</param>
    [Route("/device/{deviceId}/today")]
    public async Task<IActionResult> GetTodayData(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest("Device ID is required");
        }

        try
        {
            var today = GetVietnamNow();
            var (deviceInfo, pvData, batData, essentialLoad, grid, load) =
                await _client.GetAllDeviceDataAsync(deviceId, today);

            if (pvData == null)
            {
                return NotFound($"No data found for device {deviceId}");
            }

            var result = new
            {
                DeviceId = deviceId,
                Date = today.ToString("yyyy-MM-dd"),
                SolarKwh = (pvData.TableValue) / 10.0,
                LoadKwh = (load?.TableValue ?? 0) / 10.0,
                GridKwh = (grid?.TableValue ?? 0) / 10.0,
                BatChargeKwh = batData?.Bats != null && batData.Bats.Count > 0 
                    ? (batData.Bats[0].TableValue) / 10.0 : 0,
                BatDischargeKwh = batData?.Bats != null && batData.Bats.Count > 1 
                    ? (batData.Bats[1].TableValue) / 10.0 : 0,
                EssentialLoadKwh = (essentialLoad?.TableValue ?? 0) / 10.0
            };

            return Json(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting today data for {DeviceId}", deviceId);
            return StatusCode(500, "An error occurred");
        }
    }

    /// <summary>
    /// Gets summary data for a device within a date range
    /// </summary>
    /// <param name="deviceId">The device ID</param>
    /// <param name="from">Start date (yyyy-MM-dd)</param>
    /// <param name="to">End date (yyyy-MM-dd)</param>
    [Route("/device/{deviceId}/summary")]
    public async Task<IActionResult> GetSummaryData(string deviceId, string? from, string? to)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest("Device ID is required");
        }

        try
        {
            var vnNow = GetVietnamNow();
            var fromDate = string.IsNullOrEmpty(from)
                ? vnNow.AddMonths(-1)
                : DateTime.Parse(from);
            var toDate = string.IsNullOrEmpty(to)
                ? vnNow
                : DateTime.Parse(to);

            if (fromDate > toDate)
            {
                (fromDate, toDate) = (toDate, fromDate);
            }
            if ((toDate - fromDate).TotalDays > 90)
            {
                fromDate = toDate.AddDays(-90);
                Log.Warning("Khoảng ngày vượt quá 90 ngày, tự động giới hạn 90 ngày gần nhất");
            }

            var dailyData = new List<object>();
            var monthlyTotals = new Dictionary<string, (double load, double grid, double pv, int days)>();

            for (var date = fromDate; date <= toDate; date = date.AddDays(1))
            {
                try
                {
                    var (deviceInfo, pvData, batData, essentialLoad, grid, load) =
                        await _client.GetAllDeviceDataAsync(deviceId, date);

                    if (pvData != null)
                    {
                        var monthKey = date.ToString("yyyy-MM");
                        var loadKwh = (load?.TableValue ?? 0) / 10.0;
                        var gridKwh = (grid?.TableValue ?? 0) / 10.0;
                        var pvKwh = (pvData.TableValue) / 10.0;

                        if (!monthlyTotals.ContainsKey(monthKey))
                        {
                            monthlyTotals[monthKey] = (0, 0, 0, 0);
                        }

                        var current = monthlyTotals[monthKey];
                        monthlyTotals[monthKey] = (
                            current.load + loadKwh,
                            current.grid + gridKwh,
                            current.pv + pvKwh,
                            current.days + 1
                        );

                        dailyData.Add(new
                        {
                            Date = date.ToString("yyyy-MM-dd"),
                            LoadKwh = loadKwh,
                            GridKwh = gridKwh,
                            PvKwh = pvKwh
                        });
                    }
                }
                catch
                {
                    // Skip days with no data
                }
            }

            var monthlyData = monthlyTotals.Select(m => new
            {
                Month = m.Key,
                Load = Math.Round(m.Value.load, 1),
                Grid = Math.Round(m.Value.grid, 1),
                Pv = Math.Round(m.Value.pv, 1),
                Days = m.Value.days
            }).OrderBy(m => m.Month).ToList();

            return Json(new
            {
                DeviceId = deviceId,
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                TotalDays = dailyData.Count,
                MonthlyData = monthlyData,
                DailyData = dailyData
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting summary data for {DeviceId}", deviceId);
            return StatusCode(500, "An error occurred");
        }
    }

    /// <summary>
    /// Gets monthly data for calculator
    /// </summary>
    /// <param name="deviceId">The device ID</param>
    [Route("/device/{deviceId}/monthly")]
    [Route("/api/monthly/{deviceId}")]
    public async Task<IActionResult> GetMonthlyData(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest("Device ID is required");
        }

        try
        {
            var lehtClient = new LehtApiClient();
            var (lehtUser, lehtPass) = GetLehtCredentials();
            var loggedIn = await lehtClient.LoginAsync(lehtUser, lehtPass);
            var monthStr = GetVietnamNow().ToString("yyyy-MM");
            if (loggedIn)
            {
                var monthData = await lehtClient.GetMonthDataAsync(deviceId, monthStr);
                if (monthData != null)
                {
                    return Json(monthData);
                }
            }

            return Json(new {
                deviceId = deviceId,
                month = monthStr,
                dataSource = "offline",
                message = "Dữ liệu tháng hiện chưa sẵn sàng"
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting monthly data for {DeviceId}", deviceId);
            return Json(new {
                deviceId = deviceId,
                dataSource = "offline",
                message = "Dữ liệu tháng hiện chưa sẵn sàng"
            });
        }
    }

    /// <summary>
    /// Gets SOC timeline data for SOC chart
    /// Returns timeline array with {soc, t} for each 5-minute interval
    /// </summary>
    /// <param name="deviceId">The device ID</param>
    /// <param name="date">Date in format yyyy-MM-dd</param>
    [Route("/device/{deviceId}/soc")]
    public async Task<IActionResult> GetSOCData(string deviceId, string? date)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest("Device ID is required");
        }

        var queryDate = string.IsNullOrEmpty(date)
            ? GetVietnamNow().ToString("yyyy-MM-dd")
            : date;

        try
        {
            var lehtClient = new LehtApiClient();
            var (lehtUser, lehtPass) = GetLehtCredentials();
            var loggedIn = await lehtClient.LoginAsync(lehtUser, lehtPass);

            if (loggedIn)
            {
                var batSocData = await lehtClient.GetBatSocAsync(deviceId, queryDate);

                if (batSocData?.BatSoc?.TableValueInfo != null && batSocData.BatSoc.TableValueInfo.Count > 0)
                {
                    var timeline = new List<object>();
                    var baseTime = DateTime.TryParseExact(queryDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt) ? dt : GetVietnamNow().Date;

                    for (int i = 0; i < batSocData.BatSoc.TableValueInfo.Count; i++)
                    {
                        var time = baseTime.AddMinutes(i * 5);
                        timeline.Add(new {
                            soc = batSocData.BatSoc.TableValueInfo[i],
                            t = time.ToString("HH:mm")
                        });
                    }

                    Log.Information("Successfully fetched SOC data from LEHT API for device {DeviceId}", deviceId);
                    return Json(new {
                        deviceId = deviceId,
                        date = queryDate,
                        dataSource = "lehtapi.suntcn.com",
                        timeline = timeline
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("LEHT API SOC not available: {Msg}", ex.Message);
        }

        // Fallback: Reconstruct historical SOC timeline using battery power curve and current telemetry
        try
        {
            _ = _solarMonitor.AddDevice(deviceId);
            var parsedDate = DateTime.TryParseExact(queryDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt) ? dt : GetVietnamNow().Date;
            var (_, _, batData, _, _, _) = await _client.GetAllDeviceDataAsync(deviceId, parsedDate);
            var isToday = parsedDate.Date == GetVietnamNow().Date;
            var mqttData = isToday ? _solarMonitor.GetCachedData(deviceId) : null;
            double batVoltage = mqttData?.BatteryVoltage ?? 0;
            int currentSoc = mqttData?.BatteryPercent ?? 0;
            if (currentSoc <= 0 && batVoltage > 40)
            {
                currentSoc = CalculateLiFePO4Soc(batVoltage);
            }

            var batPowerList = batData?.TableValueInfo;
            const double defaultPackCapacityWh = 5000.0;
            var now = GetVietnamNow();
            int currentMinuteOfDay = isToday ? Math.Max(0, now.Hour * 60 + now.Minute) : 24 * 60;
            int maxInterval = (batPowerList != null && batPowerList.Count > 0)
                ? Math.Clamp(currentMinuteOfDay / 5, 1, Math.Min(batPowerList.Count, 288))
                : 0;

            if (currentSoc <= 0 && batPowerList != null && maxInterval > 0)
            {
                // Baseline estimate from accumulated net energy today into a 5kWh battery pack (base morning SOC ~25%)
                double netEnergyWh = 0;
                for (int i = 0; i < maxInterval; i++)
                {
                    netEnergyWh += (-batPowerList[i]) * (5.0 / 60.0);
                }
                currentSoc = (int)Math.Clamp(25.0 + (netEnergyWh / defaultPackCapacityWh) * 100.0, 15.0, 100.0);
            }

            if (currentSoc > 0 && batPowerList != null && batPowerList.Count > 0 && maxInterval > 0)
            {
                var timeline = new List<object>();
                var baseTime = parsedDate.Date;

                // Reconstruct backwards from current SOC based on 5-minute battery energy flow (nominal 5kWh pack)
                var socValues = new double[maxInterval];
                socValues[maxInterval - 1] = currentSoc;

                for (int i = maxInterval - 2; i >= 0; i--)
                {
                    int powerWatts = batPowerList[i];
                    // negative power = charging (earlier SOC was lower); positive = discharging (earlier SOC was higher)
                    double deltaSoc = (powerWatts * (5.0 / 60.0) / defaultPackCapacityWh) * 100.0;
                    socValues[i] = Math.Clamp(socValues[i + 1] + deltaSoc, 10.0, 100.0);
                }

                for (int i = 0; i < maxInterval; i++)
                {
                    var time = baseTime.AddMinutes(i * 5);
                    timeline.Add(new {
                        soc = (int)Math.Round(socValues[i]),
                        t = time.ToString("HH:mm")
                    });
                }

                if (timeline.Count > 0)
                {
                    return Json(new {
                        deviceId = deviceId,
                        date = queryDate,
                        dataSource = "estimated_telemetry",
                        timeline = timeline
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to generate fallback SOC timeline: {Msg}", ex.Message);
        }

        return Json(new {
            deviceId = deviceId,
            date = queryDate,
            dataSource = "offline",
            timeline = Array.Empty<object>(),
            message = "Dữ liệu SOC theo giờ tạm thời chưa có sẵn từ máy chủ LEHT"
        });
    }

    /// <summary>
    /// Gets SOC timeline data using format /api/soc/{deviceId}/{date}
    /// Primary source: lehtapi.suntcn.com
    /// Returns timeline array with {soc, t} for each 5-minute interval
    /// </summary>
    /// <param name="deviceId">The device ID</param>
    /// <param name="date">Date in format yyyy-MM-dd</param>
    [Route("/api/soc/{deviceId}/{date}")]
    public async Task<IActionResult> GetSOCDataByPath(string deviceId, string date)
    {
        return await GetSOCData(deviceId, date);
    }

    /// <summary>
    /// Debug endpoint to test connectivity to Lumentree API
    /// Accepts optional deviceId query parameter for testing specific device
    /// </summary>
    [Route("/debug/connectivity")]
    public async Task<IActionResult> TestConnectivity([FromQuery] string? deviceId = null)
    {
        var results = new Dictionary<string, object>();
        
        // Test 1: DNS Resolution
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync("lesvr.suntcn.com");
            results["dns_resolution"] = new { 
                success = true, 
                addresses = addresses.Select(a => a.ToString()).ToArray() 
            };
        }
        catch (Exception ex)
        {
            results["dns_resolution"] = new { success = false, error = ex.Message };
        }
        
        // Test 2: HTTP Connection to Lumentree API
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync("http://lesvr.suntcn.com/lesvr/getServerTime");
            var content = await response.Content.ReadAsStringAsync();
            results["lumentree_api"] = new { 
                success = response.IsSuccessStatusCode, 
                status_code = (int)response.StatusCode,
                response = content.Length > 500 ? content.Substring(0, 500) : content
            };
        }
        catch (Exception ex)
        {
            results["lumentree_api"] = new { success = false, error = ex.Message };
        }
        
        // Test 3: Token Generation (only if deviceId is provided)
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var token = await _client.GenerateToken(deviceId);
                results["token_generation"] = new { 
                    success = !string.IsNullOrEmpty(token),
                    device_id = deviceId,
                    token_preview = token?.Substring(0, Math.Min(8, token?.Length ?? 0)) + "..." 
                };
            }
            catch (Exception ex)
            {
                results["token_generation"] = new { success = false, error = ex.Message, device_id = deviceId };
            }
        }
        else
        {
            results["token_generation"] = new { 
                success = false, 
                message = "No deviceId provided. Add ?deviceId=YOUR_DEVICE_ID to test token generation" 
            };
        }
        
        return Json(results);
    }

    /// <summary>
    /// Returns an error view
    /// </summary>
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        Log.Warning("Error page requested. RequestId: {RequestId}",
            Activity.Current?.Id ?? HttpContext.TraceIdentifier);

        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
