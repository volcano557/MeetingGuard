using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAgent.App.Data;
using MeetingAgent.Core;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeetingAgent.App.ViewModels;

public partial class MainViewModel
{
    // ── 全局导航 ─────────────────────────────────────────────
    [ObservableProperty] private int selectedTabIndex = 0;
    [ObservableProperty] private Brush aigcStatusColor = Brushes.SlateGray;
    [ObservableProperty] private string aigcStatusLabel = "AIGC 检测端口待接入";

    [RelayCommand]
    private void Nav(string param)
    {
        if (int.TryParse(param, out var idx)) SelectedTabIndex = idx;
    }

    // ─── 数据库服务 ──────────────────────────────────────────
    private DatabaseService? _db;
    public void InitDb(DatabaseService db)
    {
        _db = db;
        // 登录窗已经验证过用户身份并写入 App.CurrentUser
        if (App.CurrentUser is { } u)
        {
            _currentUser = u;
            PopulateUserProfile(u);
            IsLoggedIn   = true;
            AuthLogs.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"),
                u.Username, "登录", u.Role, "成功", "通过登录窗验证"));
        }

        // 当 FaceProtect 拉到后端真实模板列表时，自动刷新身份认证 Tab 的统计
        if (FaceProtect?.UserList is { } ul)
        {
            ul.CollectionChanged += (_, _) =>
                _dispatcher.InvokeAsync(RefreshUserProfileStats);
        }
    }

    // ── 工作台 ───────────────────────────────────────────────
    [ObservableProperty] private string meetingName = "远程面试安全监测会议";
    [ObservableProperty] private string meetingId   = $"MTG-{DateTime.Now:yyyyMMdd}-A39F";

    public ObservableCollection<RecentMeetingItem> RecentMeetings { get; } = new()
    {
        new("远程面试安全监测会议", "今天 14:00",  "李某", "2 条", "待复核"),
        new("企业招聘复盘会议",     "昨天 10:30",  "赵某", "0 条", "正常"),
        new("跨境合作方视频沟通",   "2 天前 16:20","王某", "1 条", "已处置"),
        new("季度安全策略评审",     "3 天前 09:30","admin","0 条", "已归档"),
    };

    public string DashboardRiskCountText => "2";
    public string DashboardServiceText   => "3 / 4";

    [RelayCommand]
    private void EnterMeeting()
    {
        if (_db != null && _currentUser != null)
        {
            _db.SaveMeeting(MeetingId, _currentUser.Id, MeetingName);
            AddAuthLog("创建会议", "成功", MeetingId);
            RefreshUserProfileStats();
        }
        mgToast($"已创建会议 {MeetingId}，正在启动安全监测…", MessageBoxImage.Information);
        SelectedTabIndex = 4;
    }

    // ════════════════════════════════════════════════════
    // Tab 1: 身份认证（已由 LoginWindow 完成登录）
    // ════════════════════════════════════════════════════
    private DbUser? _currentUser;

    [ObservableProperty] private bool   isLoggedIn = true;   // 主窗启动时一定已登录

    [ObservableProperty] private string userDisplayName        = "";
    [ObservableProperty] private string userRoleText           = "";
    [ObservableProperty] private string userAvatarLetter       = "?";
    [ObservableProperty] private string userLoginTimeText      = "";
    [ObservableProperty] private string userTemplateCountText  = "0 个扰动模板";
    [ObservableProperty] private string userDetectionCountText = "0 次检测";
    [ObservableProperty] private string userMeetingCountText   = "0 场会议";
    [ObservableProperty] private Brush  userAvatarBrush        = Brushes.SlateGray;
    [ObservableProperty] private string userCreatedAtText      = "";

    public ObservableCollection<UserTemplateRecord>  UserTemplates        { get; } = new();
    public ObservableCollection<UserDetectionRecord> UserDetectionHistory { get; } = new();
    public ObservableCollection<AuthLogItem>         AuthLogs             { get; } = new()
    {
        new("14:21:45","admin01",  "登录",     "系统管理员","成功",""),
        new("14:18:32","hr_zhang", "入会验证", "面试官",   "成功","MTG-20260511"),
        new("14:15:10","guest03",  "2FA 触发", "访客",    "失败","MTG-20260511 · 验证码错误"),
        new("13:58:14","admin01",  "创建会议", "系统管理员","成功","MTG-20260511"),
        new("10:30:08","secops_li","密码重置", "安全审计员","成功",""),
    };

    public ObservableCollection<AuthPolicyItem> AuthPolicies { get; } = new()
    {
        new("双因子认证（2FA）", "TOTP / 短信验证，高风险自动触发", "#35D39926","#35D39966","#35D399","已启用"),
        new("会议口令验证",      "每场会议独立密码",                "#35D39926","#35D39966","#35D399","已启用"),
        new("SSO 单点登录",      "企业内部 SAML / OAuth2",         "#67B7FF26","#67B7FF66","#67B7FF","已启用"),
        new("IP 地理位置校验",   "异常地理位置触发风险",            "#F6C95F26","#F6C95F66","#F6C95F","已配置"),
        new("设备指纹绑定",      "首次登录绑定设备 UUID",           "#FFFFFF0F","#FFFFFF1F","#9FB3C8","未启用"),
    };

    // 累计：每次 AIGC 检测增量更新（避免数据库查询）
    private int _sessionDetectionCount;

    private void PopulateUserProfile(DbUser user)
    {
        UserDisplayName  = user.Username;
        UserAvatarLetter = user.Username.Length > 0 ? user.Username[0].ToString().ToUpper() : "?";
        UserRoleText     = user.Role;
        UserLoginTimeText  = $"登录于 {DateTime.Now:MM-dd HH:mm}";
        UserCreatedAtText  = $"注册于 {user.CreatedAt:yyyy-MM-dd}";
        UserAvatarBrush    = new SolidColorBrush(user.Role.Contains("审计") || user.Role == "admin"
            ? Color.FromRgb(0x58, 0xA6, 0xFF)
            : Color.FromRgb(0x35, 0xD3, 0x99));

        RefreshUserProfileStats();
    }

    /// <summary>
    /// 仅刷新统计与日志相关数据，不重写头像 / 显示名等静态字段。
    /// 可在任意操作后调用，实时反映会议数、模板数、检测数变化。
    /// 扰动模板数以 FaceProtect 后端真实列表为准（_db 表只是历史镜像）。
    /// </summary>
    public void RefreshUserProfileStats()
    {
        if (_currentUser is null) return;

        int meetingCount = _db?.GetUserMeetingCount(_currentUser.Id) ?? 0;
        // 扰动模板：优先取 FaceProtect 后端拉到的真实列表（UserList），
        // 否则回退到本地数据库镜像。
        int tplCount;
        if (FaceProtect?.UserList is { Count: > 0 } realList)
        {
            tplCount = realList.Count;
        }
        else
        {
            tplCount = _db?.GetUserFaceTemplateCount(_currentUser.Id) ?? 0;
        }
        UserMeetingCountText   = $"{meetingCount} 场会议";
        UserTemplateCountText  = $"{tplCount} 个扰动模板";
        int detCount = _sessionDetectionCount + meetingCount * 8;
        UserDetectionCountText = $"{detCount} 次检测";

        UserTemplates.Clear();
        // 优先用后端的真实模板列表
        if (FaceProtect?.UserList is { Count: > 0 } liveList)
        {
            foreach (var name in liveList)
                UserTemplates.Add(new(name, $"{name} 的扰动模板",
                    DateTime.Now.ToString("MM-dd"), "✓ 模板就绪", 0));
        }
        else if (_db != null)
        {
            foreach (var t in _db.GetFaceTemplates(_currentUser.Id))
                UserTemplates.Add(new(t.Id, t.Description, t.CreatedAt.ToString("MM-dd"), "✓ 已验证", t.PhotoCount));
        }

        UserDetectionHistory.Clear();
        if (_db != null)
        {
            foreach (var m in _db.GetUserMeetings(_currentUser.Id, 8))
            {
                var timeAgo = (DateTime.Now - (DateTime.TryParse(m.StartedAt, out var d) ? d : DateTime.Now)).TotalDays;
                var timeText = timeAgo < 1 ? "今天" : timeAgo < 2 ? "昨天" : $"{(int)timeAgo} 天前";
                UserDetectionHistory.Add(new(m.Id, timeText, "AIGC", m.MaxRisk, "已处置"));
            }
        }
    }

    /// <summary>由 OnStatusChanged 等异步路径调用：累加本次检测数并触发统计刷新。</summary>
    internal void IncrementSessionDetection(int delta)
    {
        if (delta <= 0) return;
        _sessionDetectionCount += delta;
        // 仅更新计数字段，避免清空 ObservableCollection 引起 UI 抖动
        if (_currentUser != null)
        {
            int meetingCount = _db?.GetUserMeetingCount(_currentUser.Id) ?? 0;
            UserDetectionCountText = $"{_sessionDetectionCount + meetingCount * 8} 次检测";
        }
    }

    /// <summary>记录认证 / 操作事件到 AuthLogs（实时显示）。</summary>
    internal void AddAuthLog(string action, string result = "成功", string detail = "")
    {
        if (_currentUser is null) return;
        AuthLogs.Insert(0, new AuthLogItem(
            DateTime.Now.ToString("HH:mm:ss"),
            _currentUser.Username, action, _currentUser.Role, result, detail));
        while (AuthLogs.Count > 30) AuthLogs.RemoveAt(AuthLogs.Count - 1);
    }

    [RelayCommand]
    private void DoLogout()
    {
        if (_currentUser != null)
            AuthLogs.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), _currentUser.Username,
                "退出登录", _currentUser.Role, "成功", ""));
        ((App)Application.Current).RequestLogout();
    }

    [RelayCommand] private void SaveAuthPolicy() => mgToast("认证策略已保存", MessageBoxImage.Information);

    // ════════════════════════════════════════════════════
    // Tab 2: 音频采集可视化
    // ════════════════════════════════════════════════════
    [ObservableProperty] private double audioBar0  = 0; [ObservableProperty] private double audioBar1  = 0;
    [ObservableProperty] private double audioBar2  = 0; [ObservableProperty] private double audioBar3  = 0;
    [ObservableProperty] private double audioBar4  = 0; [ObservableProperty] private double audioBar5  = 0;
    [ObservableProperty] private double audioBar6  = 0; [ObservableProperty] private double audioBar7  = 0;
    [ObservableProperty] private double audioBar8  = 0; [ObservableProperty] private double audioBar9  = 0;
    [ObservableProperty] private double audioBar10 = 0; [ObservableProperty] private double audioBar11 = 0;
    [ObservableProperty] private string audioLevelDbText = "–∞ dB";
    [ObservableProperty] private bool   isAudioActive   = false;
    [ObservableProperty] private string audioBtnText = "▶ 开启音频采集";
    [ObservableProperty] private string audioDeviceHint = "采集模式：环回+麦克风（appsettings Audio.CaptureMode）";

    private bool _tab2StartedAudioCapture;

    /// <summary>订阅真实音频包，驱动 Tab2 电平条（替代随机模拟）。</summary>
    private void InitAudioLevelHook()
    {
        _audioCaptureService.AudioReady += OnAudioLevelReady;
        _audioCaptureService.CaptureError += (_, msg) =>
            _dispatcher.InvokeAsync(() => AudioDeviceHint = $"⚠ {msg}");
    }

    private void OnAudioLevelReady(object? sender, AudioPacket pkt)
    {
        if (!IsAudioActive || pkt.Data.Length < 4)
        {
            return;
        }

        var peak = ComputePeak(pkt.Data);
        _dispatcher.InvokeAsync(() => UpdateAudioBars(peak));
    }

    private static double ComputePeak(byte[] pcm16)
    {
        double peak = 0;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            peak = Math.Max(peak, Math.Abs(s / 32768.0));
        }
        return peak;
    }

    private void UpdateAudioBars(double peak)
    {
        double[] f = { 0.3, 0.45, 0.6, 0.75, 0.85, 0.95, 0.9, 0.8, 0.65, 0.5, 0.35, 0.25 };
        AudioBar0  = Math.Min(100, peak * (f[0]  + 0.08) * 100);
        AudioBar1  = Math.Min(100, peak * (f[1]  + 0.08) * 100);
        AudioBar2  = Math.Min(100, peak * (f[2]  + 0.06) * 100);
        AudioBar3  = Math.Min(100, peak * (f[3]  + 0.05) * 100);
        AudioBar4  = Math.Min(100, peak * (f[4]  + 0.05) * 100);
        AudioBar5  = Math.Min(100, peak * (f[5]  + 0.03) * 100);
        AudioBar6  = Math.Min(100, peak * (f[6]  + 0.05) * 100);
        AudioBar7  = Math.Min(100, peak * (f[7]  + 0.06) * 100);
        AudioBar8  = Math.Min(100, peak * (f[8]  + 0.08) * 100);
        AudioBar9  = Math.Min(100, peak * (f[9]  + 0.08) * 100);
        AudioBar10 = Math.Min(100, peak * (f[10] + 0.08) * 100);
        AudioBar11 = Math.Min(100, peak * (f[11] + 0.10) * 100);
        var db = peak > 0.0001 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        AudioLevelDbText = double.IsNegativeInfinity(db) ? "–∞ dB" : $"{db:F1} dB";
    }

    private void ResetAudioBars()
    {
        AudioBar0 = AudioBar1 = AudioBar2  = AudioBar3  =
        AudioBar4 = AudioBar5 = AudioBar6  = AudioBar7  =
        AudioBar8 = AudioBar9 = AudioBar10 = AudioBar11 = 0;
        AudioLevelDbText = "–∞ dB";
    }

    [RelayCommand]
    private async Task ToggleAudioAsync()
    {
        if (IsAudioActive)
        {
            IsAudioActive = false;
            AudioBtnText = "▶ 开启音频采集";
            ResetAudioBars();

            if (_tab2StartedAudioCapture && string.IsNullOrEmpty(_semSessionId))
            {
                await _audioCaptureService.StopAsync(CancellationToken.None);
                _tab2StartedAudioCapture = false;
                AudioDeviceHint = "音频采集已停止";
            }
            return;
        }

        if (!_audioOptions.Value.Enabled)
        {
            AudioDeviceHint = "⚠ 请在 appsettings.json 中将 Audio.Enabled 设为 true 并重启";
            return;
        }

        if (!_audioCaptureService.IsRunning)
        {
            await _audioCaptureService.StartAsync(CancellationToken.None);
            _tab2StartedAudioCapture = _audioCaptureService.IsRunning;
        }
        else
        {
            // 已由语义模块等启动，Tab2 只展示电平，停止时不关采集
            _tab2StartedAudioCapture = false;
        }

        if (_audioCaptureService.IsRunning)
        {
            IsAudioActive = true;
            AudioBtnText = "⏹ 停止音频采集";
            AudioDeviceHint = "采集中：环回（耳机/扬声器播放声）+ 麦克风";
        }
        else
        {
            AudioDeviceHint = "⚠ 音频采集启动失败，请检查 Windows 麦克风/扬声器权限与默认设备";
        }
    }

    // ════════════════════════════════════════════════════
    // Tab 4: AIGC 异常分数波形
    // ════════════════════════════════════════════════════
    [ObservableProperty] private string scoreWaveformPoints  = "";
    [ObservableProperty] private string currentScoreDispText = "0.000";
    [ObservableProperty] private string waveformSessionLabel = "检测波形：等待帧…";

    private readonly List<double> _scoreHistory = new();
    private const int ScoreHistoryMax = 80;

    internal void PushScorePoint(double score)
    {
        // 严格 clamp 到 [0, 1]，防止脏数据导致 y 越界
        score = score < 0 ? 0 : (score > 1 ? 1 : score);
        _scoreHistory.Add(score);
        if (_scoreHistory.Count > ScoreHistoryMax) _scoreHistory.RemoveAt(0);
        // CurrentScore 显式 InvariantCulture，避免中文区域下 "F3" 输出"."号外的字符
        CurrentScoreDispText = score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        // Canvas 尺寸：600 × 150（与 XAML 中保持一致；增大高度让起伏更明显）
        const double cW = 600, cH = 150;
        int n = _scoreHistory.Count;
        if (n < 2) return;

        // Polyline.Points 解析对小数点严格要求 "."，必须用 InvariantCulture
        var sb = new StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < n; i++)
        {
            double x = i * cW / (ScoreHistoryMax - 1);
            double v = _scoreHistory[i];
            v = v < 0 ? 0 : (v > 1 ? 1 : v);
            double y = cH - v * cH;
            sb.Append(x.ToString("F1", inv)).Append(',')
              .Append(y.ToString("F1", inv)).Append(' ');
        }
        ScoreWaveformPoints  = sb.ToString().Trim();
        WaveformSessionLabel = $"已记录 {n} 帧 | {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void ExportDetectionData()
    {
        var path = AskSavePath("AIGC_detection.csv", "CSV 数据 (*.csv)|*.csv");
        if (path is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("帧序号,时间,风险等级,分数");
        int i = _scoreHistory.Count;
        foreach (var s in _scoreHistory.AsEnumerable().Reverse())
        {
            sb.AppendLine($"{i--},{DateTime.Now:yyyy-MM-dd HH:mm:ss},{ScoreToLevel(s)},{s.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        mgToast($"检测数据已导出：{path}", MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ExportDetectionReport()
    {
        var path = AskSavePath("AIGC_report.txt", "文本报告 (*.txt)|*.txt|Markdown (*.md)|*.md");
        if (path is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("# MeetingGuard · AIGC 视频检测报告").AppendLine();
        sb.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 当前用户：{(_currentUser?.Username ?? "N/A")}");
        sb.AppendLine($"- 会议 ID：{MeetingId}");
        sb.AppendLine($"- 累计检测帧：{_scoreHistory.Count}");
        sb.AppendLine($"- 当前风险：{RiskBadgeText}").AppendLine();
        sb.AppendLine("## 风险等级分布").AppendLine();
        var groups = _scoreHistory.GroupBy(ScoreToLevel)
            .OrderByDescending(g => g.Count())
            .Select(g => $"- {g.Key}：{g.Count()} 帧");
        foreach (var line in groups) sb.AppendLine(line);
        sb.AppendLine().AppendLine("## 最近检测记录").AppendLine();
        foreach (var d in RecentDetections.Take(20))
            sb.AppendLine($"- {d.TimeText}  {d.IndexText}  {d.RiskText}  分数 {d.ScoreText}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        mgToast($"AIGC 报告已生成：{path}", MessageBoxImage.Information);
    }

    private static string ScoreToLevel(double s) => s switch
    {
        >= 0.85 => "Critical",
        >= 0.65 => "High",
        >= 0.40 => "Medium",
        >= 0.20 => "Low",
        _       => "Info",
    };

    /// <summary>统一弹出"另存为"对话框，取消则返回 null。</summary>
    private static string? AskSavePath(string defaultName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = defaultName,
            Filter   = filter,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // ════════════════════════════════════════════════════
    // Tab 5: 语义风险识别（接入 audio_server :8003 后端）
    // ════════════════════════════════════════════════════
    [ObservableProperty] private string semanticGatewayUrl = "http://127.0.0.1:8003";
    [ObservableProperty] private string semanticStatusText = "未连接";
    [ObservableProperty] private Brush  semanticStatusBg   = Brushes.SlateGray;
    [ObservableProperty] private string semanticEventCount = "0 条";

    public ObservableCollection<SpeakerItem>       Speakers           { get; } = new();
    public ObservableCollection<SemanticEventItem> SemanticRiskEvents { get; } = new();
    public ObservableCollection<string>            TranscriptLines    { get; } = new();

    private int _semHighCount, _semMedCount, _semLowCount, _semInfoCount;
    private DateTime _semSessionStart;
    private string?  _semSessionId;
    private bool     _semStartedAudioCapture;
    private DispatcherTimer? _semPollTimer;
    private readonly HashSet<string> _semSeenTranscript = new();
    private readonly HashSet<string> _semSeenRisks      = new();

    // 音频缓冲：累积 ~1.5s 上传一次
    private readonly List<byte> _semAudioBuf = new();
    private int _semAudioSampleRate = 16000;
    private int _semAudioChannels   = 1;
    private int _semAudioBits       = 16;
    private const int SemChunkSeconds = 2;

    [ObservableProperty] private bool   showSemanticSummary = false;
    [ObservableProperty] private string semPieHighPath = ""; [ObservableProperty] private string semPieMedPath  = "";
    [ObservableProperty] private string semPieLowPath  = ""; [ObservableProperty] private string semPieInfoPath = "";
    [ObservableProperty] private string semSumHighText  = "0"; [ObservableProperty] private string semSumMedText  = "0";
    [ObservableProperty] private string semSumLowText   = "0"; [ObservableProperty] private string semSumInfoText = "0";
    [ObservableProperty] private string semSumDurationText = "00:00";
    [ObservableProperty] private string semSumTotalText    = "0 条风险事件";

    /// <summary>由构造函数调用：订阅音频事件，仅在会话激活时转发到 audio_server。</summary>
    private void InitSemanticAudioHook()
    {
        _audioCaptureService.AudioReady += OnSemanticAudioReady;
    }

    private void OnSemanticAudioReady(object? sender, AudioPacket pkt)
    {
        if (string.IsNullOrEmpty(_semSessionId)) return;
        // 缓冲音频，约 SemChunkSeconds 秒上传一次
        _semAudioSampleRate = pkt.SampleRate;
        _semAudioChannels   = pkt.Channels;
        _semAudioBits       = pkt.BitsPerSample;
        lock (_semAudioBuf)
        {
            _semAudioBuf.AddRange(pkt.Data);
        }
        int bytesPerSecond = _semAudioSampleRate * _semAudioChannels * (_semAudioBits / 8);
        int needBytes = bytesPerSecond * SemChunkSeconds;
        byte[]? chunk = null;
        lock (_semAudioBuf)
        {
            if (_semAudioBuf.Count >= needBytes)
            {
                chunk = _semAudioBuf.GetRange(0, needBytes).ToArray();
                _semAudioBuf.RemoveRange(0, needBytes);
            }
        }
        if (chunk == null) return;
        // 异步上传，不阻塞捕获线程
        _ = Task.Run(async () =>
        {
            var sid = _semSessionId;
            if (string.IsNullOrEmpty(sid)) return;
            var wav = Semantic.SemanticHttpClient.PcmToWav(chunk, _semAudioSampleRate, _semAudioChannels, _semAudioBits);
            await _semanticHttp.UploadChunkAsync(sid, wav, _semAudioSampleRate, "meeting_audio");
        });
    }

    [RelayCommand]
    private async Task SemanticStartSessionAsync()
    {
        _semanticHttp.BaseUrl = SemanticGatewayUrl;

        // 健康检查
        var ok = await _semanticHttp.HealthAsync();
        if (!ok)
        {
            SemanticStatusBg   = Brushes.OrangeRed;
            SemanticStatusText = "✕ 后端无响应";
            TranscriptLines.Add($"[{DateTime.Now:HH:mm:ss}] ✕ 无法连接到 audio_server，请确认 {SemanticGatewayUrl} 已启动");
            return;
        }

        var sid = await _semanticHttp.StartSessionAsync($"MTG-{DateTime.Now:yyyyMMddHHmm}");
        if (string.IsNullOrEmpty(sid))
        {
            SemanticStatusBg = Brushes.OrangeRed; SemanticStatusText = "✕ 启动会话失败";
            return;
        }
        _semSessionId = sid;

        SemanticStatusBg   = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        SemanticStatusText = "● 会话运行中"; SemanticEventCount = "0 条";
        ShowSemanticSummary = false;
        SemanticRiskEvents.Clear(); TranscriptLines.Clear(); Speakers.Clear();
        _semSeenTranscript.Clear(); _semSeenRisks.Clear();
        _semHighCount = _semMedCount = _semLowCount = _semInfoCount = 0;
        _semSessionStart = DateTime.Now;
        lock (_semAudioBuf) _semAudioBuf.Clear();

        TranscriptLines.Add($"[{DateTime.Now:HH:mm:ss}] ✓ 会话已启动（{sid}），正在启动音频采集…");

        // 启动轮询：每 1 秒拉一次结果
        _semPollTimer?.Stop();
        _semPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _semPollTimer.Tick += async (_, _) => await PollSemanticResultsAsync();
        _semPollTimer.Start();

        await StartSemanticAudioCaptureAsync();
    }

    private async Task StartSemanticAudioCaptureAsync()
    {
        if (!_audioOptions.Value.Enabled)
        {
            TranscriptLines.Add($"[{DateTime.Now:HH:mm:ss}] ⚠ 音频采集未启用，请在 appsettings.json 中将 Audio.Enabled 设为 true 并重启应用");
            return;
        }

        if (_semStartedAudioCapture)
        {
            IsAudioActive = true;
            AudioBtnText  = "⏹ 停止音频采集";
            TranscriptLines.Add($"[{DateTime.Now:HH:mm:ss}] ✓ 音频采集已在运行（{_audioOptions.Value.CaptureMode}）");
            return;
        }

        await _audioCaptureService.StartAsync(CancellationToken.None);
        _semStartedAudioCapture = _audioCaptureService.IsRunning;

        if (_semStartedAudioCapture)
        {
            IsAudioActive = true;
            AudioBtnText  = "⏹ 停止音频采集";
            var mode = _audioOptions.Value.CaptureMode;
            TranscriptLines.Add($"[{DateTime.Now:HH:mm:ss}] ✓ 音频采集已启动（模式：{mode}，环回=耳机/扬声器播放声，mixed 含麦克风）");
        }
        else
        {
            TranscriptLines.Add($"[{DateTime.Now:HH:mm:ss}] ⚠ 音频采集启动失败，请检查 Windows 默认音频设备与麦克风权限");
        }
    }

    private async Task StopSemanticAudioCaptureAsync()
    {
        if (!_semStartedAudioCapture || !_audioCaptureService.IsRunning)
        {
            _semStartedAudioCapture = false;
            return;
        }

        await _audioCaptureService.StopAsync(CancellationToken.None);
        _semStartedAudioCapture = false;

        if (!_tab2StartedAudioCapture)
        {
            IsAudioActive = false;
            AudioBtnText  = "▶ 开启音频采集";
            ResetAudioBars();
        }
    }

    private async Task PollSemanticResultsAsync()
    {
        if (string.IsNullOrEmpty(_semSessionId)) return;
        var data = await _semanticHttp.PollAsync(_semSessionId);
        if (data is null) return;

        // 1) 转录文本（新行才追加）
        foreach (var t in data.Transcript)
        {
            var key = $"{t.Ts}|{t.Speaker}|{t.Text}";
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            if (!_semSeenTranscript.Add(key)) continue;
            TranscriptLines.Add($"[{FormatTs(t.Ts)}] [{t.Speaker}] {t.Text}");
            while (TranscriptLines.Count > 200) TranscriptLines.RemoveAt(0);
        }

        // 2) 说话人列表
        Speakers.Clear();
        int totalChunks = 0;
        foreach (var s in data.Speakers.Values) totalChunks += s.ChunkCount;
        int idx = 0;
        foreach (var (id, info) in data.Speakers)
        {
            int percent = totalChunks > 0 ? (int)Math.Round(info.ChunkCount * 100.0 / totalChunks) : 0;
            string role  = id.Contains("00") ? "主持人" : id.Replace("SPEAKER_", "访客 ");
            string initial = role.Length > 0 ? role[0].ToString() : "?";
            string avatarBg = info.RiskCount > 0 ? "#F85149" : (idx == 0 ? "#58A6FF" : "#A98BFF");
            bool   highRisk = info.RiskCount > 0;
            Speakers.Add(new SpeakerItem(id, role, initial, avatarBg + "1A", percent,
                highRisk ? "#F8514926" : "#35D39926",
                highRisk ? "#F8514966" : "#35D39966",
                highRisk ? "#F85149"   : "#35D399",
                highRisk ? "高风险"   : "低风险"));
            idx++;
        }

        // 3) 风险事件
        foreach (var r in data.RiskEvents)
        {
            var key = $"{r.Ts}|{r.SpeakerId}|{r.Text}|{r.RiskLevel}";
            if (!_semSeenRisks.Add(key)) continue;
            var level = MapRiskLevel(r.RiskLevel);
            SemanticRiskEvents.Insert(0, new SemanticEventItem(
                FormatTs(r.Ts), r.SpeakerId, level, r.RiskType, r.Text));
            switch (level)
            {
                case "High":   _semHighCount++; break;
                case "Medium": _semMedCount++;  break;
                case "Low":    _semLowCount++;  break;
                default:       _semInfoCount++; break;
            }
        }
        SemanticEventCount = $"{SemanticRiskEvents.Count} 条";
    }

    private static string MapRiskLevel(string raw) => raw?.ToLowerInvariant() switch
    {
        "high" or "danger" or "critical" => "High",
        "medium" or "warn"               => "Medium",
        "low"                            => "Low",
        _                                => "Info",
    };

    private static string FormatTs(string iso)
    {
        if (DateTime.TryParse(iso, out var t)) return t.ToLocalTime().ToString("HH:mm:ss");
        return iso.Length >= 8 ? iso.Substring(iso.Length - 8) : iso;
    }

    [RelayCommand]
    private async Task SemanticStopSessionAsync()
    {
        _semPollTimer?.Stop(); _semPollTimer = null;
        await StopSemanticAudioCaptureAsync();
        if (!string.IsNullOrEmpty(_semSessionId))
        {
            await _semanticHttp.StopSessionAsync(_semSessionId);
        }
        _semSessionId = null;

        SemanticStatusBg = Brushes.SlateGray; SemanticStatusText = "未连接";
        TranscriptLines.Add($"[{DateTime.Now:HH:mm:ss}] ⏹ 会话已结束");

        var dur = DateTime.Now - _semSessionStart;
        SemSumDurationText = $"{(int)dur.TotalMinutes:D2}:{dur.Seconds:D2}";
        SemSumHighText = _semHighCount.ToString(); SemSumMedText  = _semMedCount.ToString();
        SemSumLowText  = _semLowCount.ToString();  SemSumInfoText = _semInfoCount.ToString();
        int total = _semHighCount + _semMedCount + _semLowCount + _semInfoCount;
        SemSumTotalText = $"{total} 条风险事件";
        if (total > 0) BuildSemanticPie(total);
        ShowSemanticSummary = total > 0;
    }

    private void BuildSemanticPie(int total)
    {
        double cx = 70, cy = 70, r = 55, start = -90;
        int[] counts = { _semHighCount, _semMedCount, _semLowCount, _semInfoCount };
        string[] p = new string[4];
        for (int i = 0; i < 4; i++)
        {
            double sw = total > 0 ? counts[i] / (double)total * 360.0 : 0;
            if (sw >= 360) sw = 359.99;
            p[i] = sw >= 0.5 ? ArcPath(cx, cy, r, start, sw) : "";
            start += sw;
        }
        SemPieHighPath = p[0]; SemPieMedPath  = p[1];
        SemPieLowPath  = p[2]; SemPieInfoPath = p[3];
    }

    [RelayCommand] private void ClearTranscript()  => TranscriptLines.Clear();

    [RelayCommand]
    private void ExportTranscript()
    {
        var path = AskSavePath($"Transcript_{DateTime.Now:yyyyMMdd_HHmm}.txt",
                               "文本 (*.txt)|*.txt|Markdown (*.md)|*.md");
        if (path is null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"# 语义风险识别 · 转录与事件  ·  {DateTime.Now:yyyy-MM-dd HH:mm:ss}").AppendLine();
        sb.AppendLine("## 转录文本").AppendLine();
        foreach (var line in TranscriptLines) sb.AppendLine(line);
        sb.AppendLine().AppendLine("## 风险事件").AppendLine();
        foreach (var ev in SemanticRiskEvents)
            sb.AppendLine($"- [{ev.Time}] {ev.Speaker} | {ev.Level} | {ev.RiskType} | {ev.Summary}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        mgToast($"转录已导出：{path}", MessageBoxImage.Information);
    }

    // ════════════════════════════════════════════════════
    // Tab 6: 数据统计
    // ════════════════════════════════════════════════════
    [ObservableProperty] private string statMeetingCount = "86";
    [ObservableProperty] private string statEventCount   = "142";
    [ObservableProperty] private string statAvgRisk      = "0.34";
    [ObservableProperty] private string statResolveRate  = "94.4%";
    [ObservableProperty] private string statSemantic = "56"; [ObservableProperty] private string statAigc = "48";
    [ObservableProperty] private string statAuth = "24";     [ObservableProperty] private string statOther = "14";
    [ObservableProperty] private string statResolved = "88"; [ObservableProperty] private string statIgnored = "32";
    [ObservableProperty] private string statFalsePos = "14"; [ObservableProperty] private string statPending = "8";

    [ObservableProperty] private double cpuUsage  = 0; [ObservableProperty] private double memUsage  = 0;
    [ObservableProperty] private double gpuUsage  = 0; [ObservableProperty] private double diskUsage = 0;
    [ObservableProperty] private string cpuText   = "-- %"; [ObservableProperty] private string memText   = "-- %";
    [ObservableProperty] private string gpuText   = "-- %"; [ObservableProperty] private string diskText  = "-- %";
    [ObservableProperty] private string netText   = "-- KB/s";
    [ObservableProperty] private string sysUptimeText = "--";

    private DispatcherTimer? _resourceTimer;
    private readonly Random  _resRnd = new(Environment.TickCount);

    public void StartResourceTimer()
    {
        _resourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _resourceTimer.Tick += (_, _) => RefreshResourceMock();
        _resourceTimer.Start(); RefreshResourceMock();
    }

    private void RefreshResourceMock()
    {
        CpuUsage  = Clamp(32 + _resRnd.NextDouble() * 30, 0, 100);
        MemUsage  = Clamp(55 + _resRnd.NextDouble() * 20, 0, 100);
        GpuUsage  = Clamp(18 + _resRnd.NextDouble() * 55, 0, 100);
        DiskUsage = Clamp(42 + _resRnd.NextDouble() * 10, 0, 100);
        CpuText  = $"{CpuUsage:F0}%"; MemText  = $"{MemUsage:F0}%";
        GpuText  = $"{GpuUsage:F0}%"; DiskText = $"{DiskUsage:F0}%";
        NetText  = $"{_resRnd.Next(30, 800)} KB/s";
        long ms = Environment.TickCount64;
        SysUptimeText = $"运行 {ms/3600000}h {ms/60000%60}m";
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    [ObservableProperty] private string trendAllPoints  = ""; [ObservableProperty] private string trendHighPoints = "";
    [ObservableProperty] private string trendLabel0 = ""; [ObservableProperty] private string trendLabel1 = "";
    [ObservableProperty] private string trendLabel2 = ""; [ObservableProperty] private string trendLabel3 = "";
    [ObservableProperty] private string trendLabel4 = "";
    [ObservableProperty] private string pieAigcPath = ""; [ObservableProperty] private string pieSemanticPath = "";
    [ObservableProperty] private string pieAuthPath = ""; [ObservableProperty] private string pieOtherPath    = "";
    [ObservableProperty] private string donutResolvedPath = ""; [ObservableProperty] private string donutIgnoredPath  = "";
    [ObservableProperty] private string donutPendingPath  = ""; [ObservableProperty] private string donutFalsePosPath = "";

    public ObservableCollection<ModuleStatItem>  ModuleStats     { get; } = new();
    public ObservableCollection<TopMeetingItem>  TopRiskMeetings { get; } = new();
    public ObservableCollection<AuditLogItem>    AuditLogs       { get; } = new();

    [RelayCommand]
    private void ExportExcel()
    {
        var path = AskSavePath($"MeetingGuard_Stats_{DateTime.Now:yyyyMMdd}.csv",
                               "CSV 表格 (*.csv)|*.csv");
        if (path is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("指标,数值");
        sb.AppendLine($"会议总数,{StatMeetingCount}");
        sb.AppendLine($"风险事件,{StatEventCount}");
        sb.AppendLine($"平均风险分,{StatAvgRisk}");
        sb.AppendLine($"处置率,{StatResolveRate}");
        sb.AppendLine();
        sb.AppendLine("模块,事件数");
        sb.AppendLine($"语义风险,{StatSemantic}");
        sb.AppendLine($"AIGC,{StatAigc}");
        sb.AppendLine($"身份认证,{StatAuth}");
        sb.AppendLine($"其他,{StatOther}");
        sb.AppendLine();
        sb.AppendLine("会议 ID,主持人,时间,参与人数,事件数,综合分,风险类型,处置状态");
        foreach (var m in TopRiskMeetings)
            sb.AppendLine($"{m.MeetingId},{m.Host},{m.Time},{m.Participants},{m.Events},{m.Score},{m.Types},{m.Status}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        mgToast($"统计数据已导出：{path}", MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ExportAudit()
    {
        var path = AskSavePath($"audit_logs_{DateTime.Now:yyyyMMdd_HHmm}.json",
                               "JSON 审计日志 (*.json)|*.json");
        if (path is null) return;
        var arr = AuditLogs.Select(a => new {
            time = a.Time, @operator = a.Operator, action = a.Action,
            target = a.Target, meeting = a.Meeting, result = a.Result
        });
        var json = System.Text.Json.JsonSerializer.Serialize(arr,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
        mgToast($"审计日志已导出：{path}", MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ExportPdf()
    {
        var path = AskSavePath($"MeetingGuard_Report_{DateTime.Now:yyyyMMdd}.md",
                               "Markdown 报告 (*.md)|*.md|文本 (*.txt)|*.txt");
        if (path is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("# MeetingGuard 数据统计报告").AppendLine();
        sb.AppendLine($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 当前用户：{(_currentUser?.Username ?? "N/A")}").AppendLine();
        sb.AppendLine("## 总览").AppendLine();
        sb.AppendLine($"| 指标 | 数值 |").AppendLine($"|------|------|");
        sb.AppendLine($"| 会议总数 | {StatMeetingCount} |");
        sb.AppendLine($"| 风险事件 | {StatEventCount} |");
        sb.AppendLine($"| 平均风险分 | {StatAvgRisk} |");
        sb.AppendLine($"| 处置率 | {StatResolveRate} |").AppendLine();
        sb.AppendLine("## 模块统计").AppendLine();
        foreach (var m in ModuleStats)
            sb.AppendLine($"- **{m.Name}**：{m.ValueText}");
        sb.AppendLine().AppendLine("## 高风险会议 Top").AppendLine();
        foreach (var m in TopRiskMeetings)
            sb.AppendLine($"- {m.MeetingId} · {m.Host} · {m.Time} · 事件 {m.Events} · {m.Score} · {m.Status}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        mgToast($"统计报告已生成：{path}", MessageBoxImage.Information);
    }

    [RelayCommand] private void RefreshResource() => RefreshResourceMock();

    public void InitStatistics()
    {
        ModuleStats.Clear();
        double mx = 12400.0;
        ModuleStats.Add(new("视频采样",    12400, mx, "#35D399","#5EE5D5","12.4 k"));
        ModuleStats.Add(new("面部隐私保护",11300, mx, "#35D399","#5EE5D5","11.3 k"));
        ModuleStats.Add(new("AIGC 检测",   2480,  mx, "#F6C95F","#FFA45C","2 480"));
        ModuleStats.Add(new("语义风险识别",9562,  mx, "#35D399","#5EE5D5","9 562"));
        ModuleStats.Add(new("身份认证",     258,  mx, "#F6C95F","#FFA45C","258"));
        ModuleStats.Add(new("说话人溯源",  1824,  mx, "#67B7FF","#A98BFF","1 824"));

        TopRiskMeetings.Clear();
        TopRiskMeetings.Add(new(1,"MTG-20260511-A39F","admin01", "05-11 14:00",4,11,"0.86","AIGC+语义","已处置"));
        TopRiskMeetings.Add(new(2,"MTG-20260509-7C12","hr_zhang","05-09 10:30",3, 8,"0.78","AIGC",     "已处置"));
        TopRiskMeetings.Add(new(3,"MTG-20260508-B8E3","secops_li","05-08 16:15",5,6,"0.62","语义",     "已处置"));
        TopRiskMeetings.Add(new(4,"MTG-20260507-D5A1","admin01", "05-07 09:00",6, 5,"0.58","身份",     "已处置"));
        TopRiskMeetings.Add(new(5,"MTG-20260506-E924","hr_wang", "05-06 14:30",3, 4,"0.52","AIGC+身份","已处置"));

        AuditLogs.Clear();
        AuditLogs.Add(new("05-11 14:21:45","admin01",  "移除参会者",     "访客 03",   "MTG-20260511-A39F","成功"));
        AuditLogs.Add(new("05-11 14:21:32","admin01",  "发起二次认证",  "SPEAKER_02","MTG-20260511-A39F","成功"));
        AuditLogs.Add(new("05-11 14:18:02","admin01",  "开启面部隐私保护","全员",     "MTG-20260511-A39F","成功"));
        AuditLogs.Add(new("05-11 13:58:14","admin01",  "创建会议",      "MTG-A39F",  "MTG-20260511-A39F","成功"));
        AuditLogs.Add(new("05-10 22:14:33","secops_li","更新敏感词库",  "新增6条",   "—",               "成功"));
        AuditLogs.Add(new("05-10 18:42:08","secops_li","调整AIGC阈值", "0.30→0.35","—",               "成功"));

        BuildTrendChart(); BuildPieChart(); BuildDonutChart();
        StartResourceTimer();
    }

    private void BuildTrendChart()
    {
        var r = new Random(42); int days = 30;
        double cW = 640, cH = 140, pL = 20, pR = 20, pT = 15, pB = 10;
        double chartW = cW - pL - pR, chartH = cH - pT - pB;
        var all  = Enumerable.Range(0, days).Select(i => (double)Math.Max(2, 14 - i/5 + r.Next(-4, 6))).ToArray();
        var high = all.Select(v => Math.Max(0, v/4.0 + r.Next(0, 2))).ToArray();
        double maxVal = Math.Max(all.Max(), 20);
        var sb1 = new StringBuilder(); var sb2 = new StringBuilder();
        for (int i = 0; i < days; i++)
        {
            double x = pL + i * chartW / (days - 1);
            sb1.Append($"{x:F1},{pT + chartH - all[i]  / maxVal * chartH:F1} ");
            sb2.Append($"{x:F1},{pT + chartH - high[i] / maxVal * chartH:F1} ");
        }
        TrendAllPoints = sb1.ToString().Trim(); TrendHighPoints = sb2.ToString().Trim();
        var now = DateTime.Now;
        TrendLabel0 = now.AddDays(-29).ToString("MM-dd"); TrendLabel1 = now.AddDays(-22).ToString("MM-dd");
        TrendLabel2 = now.AddDays(-15).ToString("MM-dd"); TrendLabel3 = now.AddDays(-7).ToString("MM-dd");
        TrendLabel4 = now.ToString("MM-dd");
    }

    private void BuildPieChart()
    {
        double total = 142, cx = 80, cy = 80, r = 60, a = -90;
        double[] c = { 48, 56, 24, 14 }; string[] p = new string[4];
        for (int i = 0; i < 4; i++) { double sw = c[i]/total*360; if (sw>=360) sw=359.99; p[i]=ArcPath(cx,cy,r,a,sw); a+=sw; }
        PieAigcPath = p[0]; PieSemanticPath = p[1]; PieAuthPath = p[2]; PieOtherPath = p[3];
    }

    private void BuildDonutChart()
    {
        double total = 142, cx = 80, cy = 80, r = 60, a = -90;
        double[] c = { 88, 32, 8, 14 }; string[] p = new string[4];
        for (int i = 0; i < 4; i++) { double sw = c[i]/total*360; if (sw>=360) sw=359.99; p[i]=ArcPath(cx,cy,r,a,sw); a+=sw; }
        DonutResolvedPath = p[0]; DonutIgnoredPath = p[1]; DonutPendingPath = p[2]; DonutFalsePosPath = p[3];
    }

    private static string ArcPath(double cx, double cy, double r, double sd, double sw)
    {
        double s = sd*Math.PI/180, e = (sd+sw)*Math.PI/180;
        return $"M {cx:F2},{cy:F2} L {cx+r*Math.Cos(s):F2},{cy+r*Math.Sin(s):F2} A {r},{r} 0 {(sw>180?1:0)} 1 {cx+r*Math.Cos(e):F2},{cy+r*Math.Sin(e):F2} Z";
    }

    private static void mgToast(string message, MessageBoxImage icon)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
            _ = Task.Delay(0).ContinueWith(_ =>
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var wnd = new Window
                    {
                        Title = "MeetingGuard", Width = 360, Height = 90,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = Application.Current.MainWindow,
                        Background = new SolidColorBrush(Color.FromRgb(0x16,0x1B,0x22)),
                        WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x30,0x36,0x3D)),
                        BorderThickness = new Thickness(1),
                    };
                    wnd.Content = new System.Windows.Controls.TextBlock
                    {
                        Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0xE6,0xED,0xF3)),
                        FontSize = 13, TextWrapping = System.Windows.TextWrapping.Wrap,
                        Margin = new Thickness(18), VerticalAlignment = VerticalAlignment.Center,
                    };
                    wnd.Show();
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                    t.Tick += (_, _) => { t.Stop(); wnd.Close(); };
                    t.Start();
                })
            )
        );
    }
}

// ════════════════════════════════════════════════════
// 数据模型
// ════════════════════════════════════════════════════
public record RecentMeetingItem(string Name, string Time, string Host, string RiskText, string Status);
public record AuthPolicyItem(string Name, string Description, string EnabledBg, string EnabledBorder, string EnabledFg, string EnabledText);
public record AuthLogItem(string Time, string User, string Action, string Role, string Result, string Detail);
public record SpeakerItem(string Name, string Role, string Initial, string AvatarBg, double SpeakPercent, string RiskBg, string RiskBorder, string RiskFg, string RiskTag);
public record SemanticEventItem(string Time, string Speaker, string Level, string RiskType, string Summary);

public record ModuleStatItem
{
    public string Name { get; } public double BarWidth { get; } public string ValueText { get; }
    public ModuleStatItem(string name, double val, double max, string c1, string c2, string vt)
    { Name = name; BarWidth = val/max*280; ValueText = vt; _ = c1; _ = c2; }
}

public record TopMeetingItem(int Rank, string MeetingId, string Host, string Time, int Participants, int Events, string Score, string Types, string Status);
public record AuditLogItem(string Time, string Operator, string Action, string Target, string Meeting, string Result);
public record UserTemplateRecord(string TemplateId, string Description, string CreatedDate, string Status, int PhotoCount);
public record UserDetectionRecord(string MeetingId, string Time, string RiskTypes, string Level, string Status);
