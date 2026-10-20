using LumenTreeInfo.API.Services;
using LumenTreeInfo.Lib;

using Serilog;
using Serilog.Events;

namespace LumenTreeInfo.API;

public class Program
{
    public static void Main(string[] args)
    {
        var contentRoot = AppContext.BaseDirectory;
        var webRoot = Path.Combine(contentRoot, "wwwroot");
        if (!Directory.Exists(webRoot))
        {
            var devWebRoot = Path.Combine(Directory.GetCurrentDirectory(), "LumenTreeInfo.API", "wwwroot");
            if (Directory.Exists(devWebRoot))
            {
                webRoot = devWebRoot;
            }
            else
            {
                var parentWebRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                if (Directory.Exists(parentWebRoot))
                {
                    webRoot = parentWebRoot;
                }
            }
        }

        var webAppOptions = new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
            WebRootPath = webRoot
        };

        var builder = WebApplication.CreateBuilder(webAppOptions);
        builder.Host.UseSerilog();

        // Configure Serilog
        SetupSerilog(builder.Configuration);

        // Add services to the container.
        builder.Services.AddControllersWithViews();

        // Add Memory Cache
        builder.Services.AddMemoryCache();

        // Add our custom caching service
        builder.Services.AddSingleton<ICacheService, MemoryCacheService>();

        // Add SignalR
        builder.Services.AddSignalR();

        // Add ProxyManager and Background Refresh Service
        builder.Services.AddSingleton<ProxyManager>();
        builder.Services.AddHostedService<ProxyBackgroundService>();

        // Add LumentreeClient as a singleton with caching and direct connection
        builder.Services.AddSingleton<LumentreeClient>(serviceProvider => {
            var cacheService = serviceProvider.GetRequiredService<ICacheService>();
            var proxyManager = serviceProvider.GetService<ProxyManager>();
            return new LumentreeClient(cacheService, proxyManager);
        });

        // Add SolarMonitorService as a singleton (depends on ProxyManager & LumentreeClient)
        builder.Services.AddSingleton<SolarMonitorService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SolarMonitorService>());

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        // Only use HTTPS redirection in production with valid certificates
        // app.UseHttpsRedirection();
        
        app.UseStaticFiles();

        // Add no-cache headers to prevent stale data issues for dynamic routes
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
            await next();
        });

        app.UseRouting();

        app.UseAuthorization();

        // Map SignalR hubs
        app.MapHub<DeviceHub>("/deviceHub");

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");

        Log.Information("Application starting up");
        app.Run();
    }

    private static void SetupSerilog(IConfiguration configuration)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "log-.log");

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .MinimumLevel.Information()
            //.MinimumLevel.Override("Microsoft", LogEventLevel.Error)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("Serilog configured with log path: {LogPath}", logPath);
    }
}// Trigger redeploy Wed Dec 10 12:43:08 UTC 2025
