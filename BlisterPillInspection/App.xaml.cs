using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using BlisterPillInspection.Infrastructure;

namespace BlisterPillInspection;

/// <summary>
/// 应用程序入口。负责 Generic Host + DI 容器构建、Serilog 日志初始化、
/// 全局未捕获异常三件套挂载（健壮性 R6）以及 MainWindow 启动。
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;

    /// <summary>
    /// 构造函数。完成日志、Host、DI、异常三件套的全部初始化。
    /// </summary>
    public App()
    {
        // 1) Serilog 早期初始化：确保后续异常均可落盘。
        var logDir = Path.Combine(AppContext.BaseDirectory, Constants.LogFolder);
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Application", nameof(BlisterPillInspection))
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        Log.Logger.Information("=== 应用启动 ===");

        // 2) Host 构建：appsettings.json 可选加载（R5：配置损坏不崩启动）。
        try
        {
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureAppConfiguration((ctx, cfg) =>
                {
                    cfg.SetBasePath(AppContext.BaseDirectory);
                    // R5: optional=true + reloadOnChange=true，文件缺失/损坏不会抛。
                    cfg.AddJsonFile(Constants.ConfigFileName, optional: true, reloadOnChange: true);
                })
                .ConfigureServices((ctx, services) =>
                {
                    services.AddApplicationServices();
                    // 注册 MainWindow 自身（其构造依赖 MainWindowViewModel）
                    services.AddSingleton<MainWindow>();
                })
                .Build();
        }
        catch (Exception ex)
        {
            // R5: 配置或 DI 构建失败时回退到内存默认服务，不崩启动。
            Log.Logger.Warning(ex, "Host 构建失败，回退到内存默认服务");
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(services =>
                {
                    services.AddApplicationServices();
                    services.AddSingleton<MainWindow>();
                })
                .Build();
        }

        // 3) 全局未捕获异常三件套（R6）
        //    - DispatcherUnhandledException: UI 线程异常，e.Handled=true 阻止崩溃
        //    - AppDomain.UnhandledException: 非 UI 线程/终结异常，仅记录
        //    - TaskScheduler.UnobservedTaskException: Task 未观察异常，SetObserved 阻止进程终结
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// 启动：先 StartAsync 启动 Host（含所有 IHostedService），再显示 MainWindow。
    /// </summary>
    /// <param name="e">启动事件参数。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await _host.StartAsync();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "应用启动失败");
            Shutdown(1);
        }
    }

    /// <summary>
    /// 退出：停止 Host（带 5 秒软停超时），刷新日志。
    /// </summary>
    /// <param name="e">退出事件参数。</param>
    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _host.StopAsync(cts.Token);
            _host.Dispose();
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Host 停止过程中抛异常");
        }
        finally
        {
            Log.Logger.Information("=== 应用退出 ===");
            Log.CloseAndFlush();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// UI 线程未捕获异常处理。记录并标记已处理以阻止进程崩溃。
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Logger.Error(e.Exception, "UI 线程未捕获异常");
        e.Handled = true;
    }

    /// <summary>
    /// AppDomain 未捕获异常处理。仅记录，通常为终结性异常。
    /// </summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Logger.Fatal(e.ExceptionObject as Exception,
            "AppDomain 未捕获异常，IsTerminating={IsTerminating}", e.IsTerminating);
    }

    /// <summary>
    /// Task 未观察异常处理。记录并标记已观察，阻止进程终结。
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Logger.Error(e.Exception, "Task 未观察异常");
        e.SetObserved();
    }
}
