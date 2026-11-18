using MeetingAgent.Alerting;
using MeetingAgent.App.Auth;
using MeetingAgent.App.Data;
using MeetingAgent.App.ViewModels;
using MeetingAgent.Audio;
using MeetingAgent.Capture;
using MeetingAgent.Infrastructure;
using MeetingAgent.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Windows;

namespace MeetingAgent.App;

public partial class App : Application
{
    private IHost? _host;
    private bool   _logoutRequested = false;

    /// <summary>登录后写入；MainViewModel 启动时可读取以确定当前用户。</summary>
    public static DbUser? CurrentUser { get; set; }

    /// <summary>暴露 DI 容器，供 ViewModel / 其它代码取依赖。</summary>
    public IServiceProvider? Services => _host?.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // ShutdownMode 默认 OnLastWindowClose；我们手工管理生命周期。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var logSink = new InMemoryLogSink();
        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration(builder =>
            {
                builder.SetBasePath(AppContext.BaseDirectory);
                builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .UseSerilog((context, _, configuration) =>
            {
                configuration.AddMeetingAgentFileLogging(context.Configuration, logSink);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddMeetingAgentInfrastructure(context.Configuration, logSink);
                services.AddMeetingAgentCapture();
                services.AddMeetingAgentAudio();
                services.AddMeetingAgentTransport();
                services.AddMeetingAgentAlerting();

                services.AddSingleton<IAgentControlService, AgentControlService>();
                services.AddSingleton<PipelineHostedService>();
                services.AddSingleton<IPipelineBufferControl>(sp => sp.GetRequiredService<PipelineHostedService>());
                services.AddHostedService(sp => sp.GetRequiredService<PipelineHostedService>());

                // 数据库
                services.AddSingleton<DatabaseService>();

                // 登录窗：每次注销 / 启动都用一个新实例
                services.AddTransient<LoginViewModel>();
                services.AddTransient<LoginWindow>();

                // 人脸隐私保护模块
                services.AddSingleton<FaceProtectHttpClient>();
                services.AddSingleton<FaceProtectViewModel>();

                // 语义风险识别 HTTP 客户端（audio_server :8003）
                services.AddSingleton<Semantic.SemanticHttpClient>();

                // 主窗：使用 Transient 以便注销后重新登录可创建全新实例
                // (PipelineHostedService 仍是 Singleton，会议管线状态不会因此中断)
                services.AddTransient<MainViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // ─── 启动流程：登录 → 主窗 ─────────────────────────────
        if (!ShowLoginAndMain())
        {
            Shutdown();
        }
    }

    /// <summary>显示登录窗，成功则显示主窗。返回 true 表示进入了主窗。</summary>
    private bool ShowLoginAndMain()
    {
        var loginWindow = _host!.Services.GetRequiredService<LoginWindow>();
        bool? ok = loginWindow.ShowDialog();
        if (ok != true || loginWindow.LoggedInUser is null) return false;

        CurrentUser = loginWindow.LoggedInUser;
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Closed += MainWindow_Closed;
        mainWindow.Show();
        return true;
    }

    /// <summary>主窗关闭事件：如果是用户主动注销，则重新弹登录窗；否则退出程序。</summary>
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_logoutRequested)
        {
            _logoutRequested = false;
            CurrentUser = null;
            if (!ShowLoginAndMain()) Shutdown();
        }
        else
        {
            Shutdown();
        }
    }

    /// <summary>由 ViewModel 调用，用户在主窗内点击"退出登录"。</summary>
    public void RequestLogout()
    {
        _logoutRequested = true;
        MainWindow?.Close();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
