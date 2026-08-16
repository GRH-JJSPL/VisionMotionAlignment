using Microsoft.Extensions.DependencyInjection;
using VisionMotionAlignment.Models;
using VisionMotionAlignment.Services.Camera;
using VisionMotionAlignment.Services.Communication;
using VisionMotionAlignment.Services.Configuration;
using VisionMotionAlignment.Services.Force;
using VisionMotionAlignment.Services.Interfaces;
using VisionMotionAlignment.Services.MotionCard;
using VisionMotionAlignment.Services.Orchestration;
using VisionMotionAlignment.Services.Vision;
using VisionMotionAlignment.ViewModels;
using VisionMotionAlignment.ViewModels.Pages;

namespace VisionMotionAlignment.Infrastructure;

/// <summary>
/// 应用服务集合的 DI 注册扩展方法。
/// </summary>
/// <remarks>
/// 力值模块和诊断页各使用独立的 <see cref="ModbusRtuTransport"/> 实例，
/// 在服务工厂闭包内构建并传递 ownsTransport=true，由服务负责 Dispose。
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 向 DI 容器注册全部应用服务与页面 ViewModel。
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // ── 业务服务（真实实现）──
        // 运动控制卡（固高 GTS 风格）：优先真实 TCP 服务，连接失败时自动回退到虚拟服务。
        // 虚拟服务不依赖 TCP/硬件，用于无真实卡或模拟器时跑通"送料→检测→分拣"联动流程。
        services.AddSingleton<IMotionCardService>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GtsMotionCardService>>();
            var real = new GtsMotionCardService();
            var virtualCard = new VirtualMotionCardService();
            return new FallbackMotionCardService(real, virtualCard, logger);
        });

        // 力值模块（独立串口，与运动控制卡无关）
        services.AddSingleton<IForceModuleService>(_ =>
            new ForceModule500BService(new ModbusRtuTransport()));

        // 诊断页 Modbus 传输层（独立实例，供诊断页手动读写寄存器）
        services.AddSingleton<IModbusRtuTransport, ModbusRtuTransport>();

        // 视觉检测：泡罩药丸检测（Halcon GMM 分类器）
        services.AddSingleton<IBlisterCheckService, BlisterCheckService>();
        services.AddSingleton<IAppConfigService, AppConfigService>();

        // 检测编排器（串联运动控制卡 + 视觉检测：送料→检测→分拣）
        services.AddSingleton<IInspectionOrchestrator>(sp =>
        {
            var blisterCheck = sp.GetRequiredService<IBlisterCheckService>();
            var motionCard = sp.GetRequiredService<IMotionCardService>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InspectionOrchestrator>>();
            return new InspectionOrchestrator(blisterCheck, motionCard, logger);
        });

        // ── 相机实例（保留 Stub，大恒 SDK DLL 缺失时使用）──
        // 工位 1 / 工位 2 各持独立 ICameraService，互不干扰
        services.AddSingleton<ICameraService>(_ => new StubCameraService());
        services.AddSingleton<ICameraService>(_ => new StubCameraService());

        // ── 页面 VM ──
        services.AddSingleton<BlisterCheckPageViewModel>();
        services.AddSingleton<CameraSettingPageViewModel>();
        services.AddSingleton<CommSettingPageViewModel>();
        services.AddSingleton<ForceMonitorPageViewModel>();
        services.AddSingleton<DiagnosticPageViewModel>();

        // 主窗口 VM
        services.AddSingleton<MainWindowViewModel>();

        return services;
    }
}
