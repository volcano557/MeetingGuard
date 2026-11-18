using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace MeetingAgent.App.ViewModels;

// 照片上传数据模型
public record PhotoThumbItem(string FileName, BitmapSource Thumb);


/// <summary>
/// 人脸隐私保护模块 ViewModel。
/// 管理离线扰动生成和在线实时摄像头保护两个子功能。
/// </summary>
public partial class FaceProtectViewModel : ObservableObject, IDisposable
{
    private readonly FaceProtectHttpClient _client;
    private readonly ILogger<FaceProtectViewModel> _logger;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    // 轮询定时器
    private readonly DispatcherTimer _generatePollTimer;
    private readonly DispatcherTimer _onlineStatusTimer;

    // 摄像头实时保护
    private VideoCapture? _capture;
    private Thread? _cameraThread;
    private volatile bool _cameraRunning;
    private CancellationTokenSource? _cameraCts; // WinRT 异步路径使用

    // ─── 连接配置 ──────────────────────────────────────────────────────────

    [ObservableProperty] private string faceProtectGatewayUrl = "http://127.0.0.1:5001";
    [ObservableProperty] private string faceProtectHost = "127.0.0.1";
    [ObservableProperty] private string faceProtectPort = "5001";
    [ObservableProperty] private string backendStatusText = "保护后端：未连接";
    [ObservableProperty] private string backendStatusColor = "#888888";
    [ObservableProperty] private bool isBackendConnected = false;

    // ─── 离线生成 ─────────────────────────────────────────────────────────

    [ObservableProperty] private string generateUserName = string.Empty;

    // 用户在"扰动生成"中改名时，自动同步到"在线保护"，避免重复输入
    partial void OnGenerateUserNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) OnlineUserName = value.Trim();
    }
    [ObservableProperty] private string generateImgsDir = string.Empty;
    [ObservableProperty] private string generateEps = "16";
    [ObservableProperty] private string generateIters = "200";
    [ObservableProperty] private string generateDevice = "cuda:0";
    [ObservableProperty] private string generateStatusText = "状态：待机";
    [ObservableProperty] private string generateStatusColor = "#888888";
    [ObservableProperty] private double generateProgress = 0;
    [ObservableProperty] private string generateProgressText = "0%";
    [ObservableProperty] private bool isGenerating = false;
    [ObservableProperty] private string generationLogText = string.Empty;

    // ─── 在线保护 ─────────────────────────────────────────────────────────

    [ObservableProperty] private string onlineUserName = string.Empty;
    [ObservableProperty] private string onlineDevice = "cuda:0";
    [ObservableProperty] private string onlineEps = "16";
    [ObservableProperty] private int onlineCameraIndex = 0;
    [ObservableProperty] private string onlineStatusText = "保护状态：未启动";
    [ObservableProperty] private string onlineStatusColor = "#888888";
    [ObservableProperty] private string onlineFramesText = "已处理帧：0";
    [ObservableProperty] private string onlineLatencyText = "延迟：--ms";
    [ObservableProperty] private bool isProtecting = false;
    [ObservableProperty] private BitmapSource? originalFrameImage;
    [ObservableProperty] private BitmapSource? protectedFrameImage;
    [ObservableProperty] private string previewStatusText = "摄像头：未启动";


    // ─── 照片上传（离线生成用） ─────────────────────────────────────────
    public ObservableCollection<PhotoThumbItem> UploadedPhotos { get; } = new();
    [ObservableProperty] private string uploadStatusText = "请上传 3-5 张不同角度的人脸照片";
    [ObservableProperty] private string uploadCountText  = "0 / 5 张";

    /// <summary>用户从本地选择的照片绝对路径，生成时统一上传到后端。</summary>
    private readonly List<string> _selectedLocalPhotoPaths = new();

    [RelayCommand]
    private void UploadPhotos()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title     = "选择人脸照片（3-5 张，不同角度）",
            Filter    = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        var files = dlg.FileNames;
        if (files.Length < 1)
            return;
        if (files.Length > 5)
        {
            MessageBox.Show("最多选择 5 张照片，已自动取前 5 张。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            files = files.Take(5).ToArray();
        }

        _selectedLocalPhotoPaths.Clear();
        UploadedPhotos.Clear();
        foreach (var file in files)
        {
            _selectedLocalPhotoPaths.Add(file);
            try
            {
                var thumb = new BitmapImage();
                thumb.BeginInit();
                thumb.UriSource = new Uri(file, UriKind.Absolute);
                thumb.CacheOption = BitmapCacheOption.OnLoad;
                thumb.DecodePixelWidth = 90;
                thumb.EndInit();
                thumb.Freeze();
                UploadedPhotos.Add(new PhotoThumbItem(Path.GetFileName(file), thumb));
            }
            catch
            {
                UploadedPhotos.Add(new PhotoThumbItem(Path.GetFileName(file),
                    BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Gray8,
                        null, new byte[] { 128 }, 1)));
            }
        }

        // 用 token 占位：真实路径会在「开始生成」时由后端返回
        GenerateImgsDir = $"[client_pending:{UploadedPhotos.Count}]";
        UploadCountText  = $"{UploadedPhotos.Count} / 5 张";
        UploadStatusText = UploadedPhotos.Count >= 3
            ? $"✓ 已选 {UploadedPhotos.Count} 张照片，将在点击生成时上传到后端"
            : $"⚠ 已选 {UploadedPhotos.Count} 张，建议至少 3 张";
    }

    [RelayCommand]
    private void ClearPhotos()
    {
        UploadedPhotos.Clear();
        _selectedLocalPhotoPaths.Clear();
        GenerateImgsDir  = "";
        UploadCountText  = "0 / 5 张";
        UploadStatusText = "请上传 3-5 张不同角度的人脸照片";
    }

    // ─── 独立预览窗口 ────────────────────────────────────────────────────
    private MeetingAgent.App.ProtectedPreviewWindow? _previewWindow;

    [ObservableProperty] private string previewWindowBtnText = "⊞ 弹出独立预览窗口";

    [RelayCommand]
    private void TogglePreviewWindow()
    {
        _dispatcher.Invoke(() =>
        {
            if (_previewWindow == null)
            {
                _previewWindow = new MeetingAgent.App.ProtectedPreviewWindow();
                _previewWindow.DataContext = this;   // 绑定 ViewModel，XAML 中所有内容由数据绑定驱动
            }

            if (_previewWindow.IsVisible)
            {
                _previewWindow.Hide();
                PreviewWindowBtnText = "⊞ 弹出独立预览窗口";
            }
            else
            {
                _previewWindow.Show();
                PreviewWindowBtnText = "⊠ 关闭独立预览窗口";
            }
        });
    }

    // ─── 用户列表 ─────────────────────────────────────────────────────────

    public ObservableCollection<string> UserList { get; } = new();

    // ─── 构造 ──────────────────────────────────────────────────────────────

    public FaceProtectViewModel(
        FaceProtectHttpClient client,
        ILogger<FaceProtectViewModel> logger)
    {
        _client = client;
        _logger = logger;

        _generatePollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _generatePollTimer.Tick += async (_, _) => await PollGenerationStatusAsync();

        _onlineStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _onlineStatusTimer.Tick += async (_, _) => await PollOnlineStatusAsync();
    }

    // ─── 连接后端 ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!BackendUrlHelper.TryParse(FaceProtectGatewayUrl, out var parsed))
        {
            MessageBox.Show("FaceProtect 地址格式不正确。\n示例：http://127.0.0.1:5001",
                "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FaceProtectHost = parsed.Host;
        FaceProtectPort = parsed.Port.ToString();
        _client.SetBaseUrl(parsed.Host, parsed.Port);
        var health = await _client.GetHealthAsync();
        if (health != null)
        {
            IsBackendConnected = true;
            BackendStatusText = $"保护后端：已连接  CUDA={health.CudaAvailable}  活跃会话：{health.ActiveSessions.Count}";
            BackendStatusColor = "#1DB954";
            await RefreshUserListAsync();
        }
        else
        {
            IsBackendConnected = false;
            BackendStatusText = "保护后端：连接失败（请确认 WSL 服务 :5001 已启动，并运行 setup_localhost_ports.ps1）";
            BackendStatusColor = "#CC3333";
            MessageBox.Show(
                "无法连接 FaceProtect 后端。\n\n"
                + "1) WSL 中运行：python face_protect_server.py --host 0.0.0.0 --port 5001\n"
                + "2) 管理员运行：tools\\setup_localhost_ports.ps1\n"
                + "3) 地址保持：http://127.0.0.1:5001",
                "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ─── 离线扰动生成 ──────────────────────────────────────────────────────

    /// <summary>
    /// 打开 Windows 文件夹选择对话框，将选中路径自动转换为 WSL2 路径（/mnt/...）写入图片目录。
    /// </summary>
    [RelayCommand]
    private void BrowseImagesDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择人脸图片文件夹（将自动转换为 WSL2 路径）",
            Multiselect = false
        };
        if (dlg.ShowDialog() == true)
            GenerateImgsDir = WindowsPathToWsl2(dlg.FolderName);
    }

    /// <summary>将 Windows 路径转换为 WSL2 挂载路径，例如 D:\photos → /mnt/d/photos。</summary>
    private static string WindowsPathToWsl2(string winPath)
    {
        if (winPath.Length >= 2 && winPath[1] == ':')
        {
            var drive = char.ToLowerInvariant(winPath[0]);
            var rest  = winPath[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }
        return winPath.Replace('\\', '/');
    }

    [RelayCommand]
    private async Task StartGenerationAsync()
    {
        if (!IsBackendConnected)
        {
            MessageBox.Show("请先连接保护后端。", "未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(GenerateUserName))
        {
            MessageBox.Show("请输入用户名。", "参数缺失", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_selectedLocalPhotoPaths.Count == 0)
        {
            MessageBox.Show("请先点击「📁 选择照片」上传 3-5 张人脸照片。", "参数缺失",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!float.TryParse(GenerateEps, out var eps))
            eps = 16f;
        if (!int.TryParse(GenerateIters, out var iters))
            iters = 200;

        try
        {
            IsGenerating = true;
            GenerateProgress = 0;
            GenerateProgressText = "0%";
            GenerateStatusText = $"状态：正在上传 {_selectedLocalPhotoPaths.Count} 张照片到后端…";
            GenerateStatusColor = "#1565C0";
            GenerationLogText = string.Empty;

            // ⭐ 核心修复：先把照片真实上传到 Linux 后端，再用后端返回的路径调用 generate
            var upload = await _client.UploadPhotosAsync(
                GenerateUserName.Trim(), _selectedLocalPhotoPaths, clearExisting: true);

            // 后端实际可访问的目录（绝对路径），覆盖之前的"占位符"
            GenerateImgsDir = upload.ImgsDir;
            GenerationLogText += $"[上传] 已保存 {upload.SavedCount} 张照片 → {upload.ImgsDir}\n";

            GenerateStatusText = "状态：正在提交生成任务…";

            await _client.StartGenerationAsync(
                GenerateUserName.Trim(), upload.ImgsDir, eps, iters, GenerateDevice);

            GenerateStatusText = "状态：生成中…";
            _generatePollTimer.Start();
        }
        catch (Exception ex)
        {
            IsGenerating = false;
            GenerateStatusText = $"状态：提交失败 — {ex.Message}";
            GenerateStatusColor = "#CC3333";
            _logger.LogWarning(ex, "StartGeneration failed");
        }
    }

    [RelayCommand]
    private async Task RefreshUserListAsync()
    {
        if (!IsBackendConnected) return;
        try
        {
            var resp = await _client.ListUsersAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                UserList.Clear();
                foreach (var u in resp.Users)
                    UserList.Add(u);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshUserList failed");
        }
    }

    private async Task PollGenerationStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(GenerateUserName)) return;
        try
        {
            var st = await _client.GetGenerationStatusAsync(GenerateUserName);
            await _dispatcher.InvokeAsync(() =>
            {
                GenerateProgress = st.Progress;
                GenerateProgressText = $"{st.Progress}%";

                // 追加最新日志行
                if (!string.IsNullOrWhiteSpace(st.Message)
                    && !GenerationLogText.EndsWith(st.Message))
                {
                    var lines = GenerationLogText.Split('\n');
                    if (lines.Length > 200)
                        GenerationLogText = string.Join('\n', lines[^100..]);
                    GenerationLogText += st.Message + "\n";
                }

                switch (st.Status)
                {
                    case "running":
                        GenerateStatusText = $"状态：生成中 {st.Progress}%";
                        GenerateStatusColor = "#1565C0";
                        break;
                    case "done":
                        GenerateProgress = 100;
                        GenerateProgressText = "100%";
                        GenerateStatusText = "状态：生成完成 ✓ 请点击「开启实时保护」加载新扰动";
                        GenerateStatusColor = "#1DB954";
                        IsGenerating = false;
                        _generatePollTimer.Stop();
                        // 标记在线会话需要重新加载（否则旧扰动仍被缓存）
                        _online_session_started = false;
                        _ = RefreshUserListAsync();
                        break;
                    case "failed":
                        GenerateStatusText = $"状态：生成失败 — {st.Message}";
                        GenerateStatusColor = "#CC3333";
                        IsGenerating = false;
                        _generatePollTimer.Stop();
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PollGenerationStatus failed");
        }
    }

    // ─── 图片文件保护（demo 保底模式） ────────────────────────────────────

    [ObservableProperty] private BitmapSource? singleOriginalImage;
    [ObservableProperty] private BitmapSource? singleProtectedImage;
    [ObservableProperty] private string singleStatusText = "图片模式：选择一张照片后点击保护";

    [RelayCommand]
    private async Task ProtectSingleImageAsync()
    {
        if (!IsBackendConnected)
        {
            MessageBox.Show("请先连接保护后端。", "未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(OnlineUserName))
        {
            MessageBox.Show("请填写保护用户名。", "参数缺失", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要保护的人脸照片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            SingleStatusText = "图片模式：读取图片...";
            var imgBytes = File.ReadAllBytes(dlg.FileName);

            // 显示原始图
            var origSrc = JpegToBitmapSource(imgBytes);
            SingleOriginalImage = origSrc;

            // 确保在线会话已启动
            if (!_online_session_started)
            {
                SingleStatusText = "图片模式：加载保护模型（首次约30秒）...";
                if (!float.TryParse(OnlineEps, out var eps2)) eps2 = 16f;
                await _client.StartOnlineAsync(OnlineUserName, OnlineDevice, eps2);
                _online_session_started = true;
            }

            SingleStatusText = "图片模式：正在保护...";
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            string b64;
            if (ext is ".jpg" or ".jpeg")
            {
                b64 = Convert.ToBase64String(imgBytes);
            }
            else
            {
                // 非 JPEG 先通过 WPF 编码器转换
                var bmpSrc = new BitmapImage(new Uri(dlg.FileName, UriKind.Absolute));
                var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(bmpSrc));
                using var ms2 = new MemoryStream();
                encoder.Save(ms2);
                b64 = Convert.ToBase64String(ms2.ToArray());
            }

            var result = await _client.ProtectFrameAsync(OnlineUserName, b64);
            var protBytes = Convert.FromBase64String(result.ProtectedFrameBase64);
            SingleProtectedImage = JpegToBitmapSource(protBytes);
            SingleStatusText = $"图片模式：保护完成  延迟 {result.LatencyMs:F0}ms";
        }
        catch (Exception ex)
        {
            SingleStatusText = $"图片模式：失败 — {ex.Message}";
            _logger.LogWarning(ex, "ProtectSingleImage failed");
        }
    }

    private bool _online_session_started = false;

    // ─── 在线实时保护 ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartOnlineAsync()
    {
        if (!IsBackendConnected)
        {
            MessageBox.Show("请先连接保护后端。", "未连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(OnlineUserName))
        {
            MessageBox.Show("请选择或输入要保护的用户名。", "参数缺失", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!float.TryParse(OnlineEps, out var eps))
            eps = 16f;

        try
        {
            OnlineStatusText = "保护状态：正在加载模型...";
            OnlineStatusColor = "#1565C0";
            IsProtecting = true;

            if (!_online_session_started)
            {
                await _client.StartOnlineAsync(OnlineUserName, OnlineDevice, eps);
                _online_session_started = true;
            }

            OnlineStatusText = "保护状态：模型已加载，启动摄像头...";
            StartCameraLoop();
            _onlineStatusTimer.Start();
            OnlineStatusText = "保护状态：保护中";
            OnlineStatusColor = "#1DB954";
        }
        catch (Exception ex)
        {
            IsProtecting = false;
            OnlineStatusText = $"保护状态：启动失败 — {ex.Message}";
            OnlineStatusColor = "#CC3333";
            _logger.LogWarning(ex, "StartOnline failed");
        }
    }

    [RelayCommand]
    private async Task StopOnlineAsync()
    {
        StopCameraLoop();
        _onlineStatusTimer.Stop();

        if (!string.IsNullOrWhiteSpace(OnlineUserName) && IsBackendConnected)
        {
            try
            {
                await _client.StopOnlineAsync(OnlineUserName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StopOnline API call failed");
            }
        }

        _online_session_started = false; // 下次启动时强制重新加载扰动
        IsProtecting = false;
        OnlineStatusText = "保护状态：已停止";
        OnlineStatusColor = "#888888";
        OnlineFramesText = "已处理帧：0";
        OnlineLatencyText = "延迟：--ms";
        OriginalFrameImage = null;
        ProtectedFrameImage = null;
        PreviewStatusText = "摄像头：已停止";
        PreviewWindowBtnText = "⊞ 弹出独立预览窗口";
        // 预览窗口通过数据绑定 ProtectedFrameImage=null 自动清空，无需手动推送
    }

    private async Task PollOnlineStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(OnlineUserName)) return;
        try
        {
            var st = await _client.GetOnlineStatusAsync(OnlineUserName);
            await _dispatcher.InvokeAsync(() =>
            {
                OnlineFramesText = $"已处理帧：{st.FramesProcessed}";
                OnlineLatencyText = $"延迟：{st.LastLatencyMs:F0}ms";
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PollOnlineStatus failed");
        }
    }

    // ─── 摄像头捕获（WinRT 照片模式优先，OpenCvSharp DirectShow 备用）───────

    private void StartCameraLoop()
    {
        StopCameraLoop();
        // WinRT 异步捕获：必须在 UI（STA）线程启动
        _cameraCts = new CancellationTokenSource();
        var cts = _cameraCts;
        _dispatcher.InvokeAsync(() => _ = WinRTCaptureLoopAsync(cts.Token));
    }

    private void StopCameraLoop()
    {
        _cameraRunning = false;
        _cameraCts?.Cancel();
        _cameraCts = null;
        _cameraThread?.Join(3000);
        _cameraThread = null;
        _capture?.Dispose();
        _capture = null;
    }

    /// <summary>
    /// 主摄像头采集循环（WinRT CapturePhotoToStreamAsync 照片模式）。
    /// <para>与 MediaFrameReader 不同，此 API 通过"照片捕获"路径逐帧拍照，
    /// 无需预览 Sink，不依赖驱动流式传输，对各种摄像头兼容性最好。</para>
    /// 若连续失败则自动降级到 OpenCvSharp DirectShow 后端。
    /// </summary>
    private async Task WinRTCaptureLoopAsync(CancellationToken ct)
    {
        await _dispatcher.InvokeAsync(() => PreviewStatusText = "摄像头：初始化 WinRT...");

        MediaCapture? mc = null;
        try
        {
            mc = new MediaCapture();
            await mc.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference      = MediaCaptureMemoryPreference.Cpu,
            });

            // 尽量接近 640×480 的 JPEG 照片
            var jpegProps = ImageEncodingProperties.CreateJpeg();
            jpegProps.Width  = 640;
            jpegProps.Height = 480;

            await _dispatcher.InvokeAsync(() =>
                PreviewStatusText = "摄像头：WinRT 照片模式运行中...");

            int failCount = 0;
            while (!ct.IsCancellationRequested)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var stream = new InMemoryRandomAccessStream();
                    await mc.CapturePhotoToStreamAsync(jpegProps, stream);

                    var size = (uint)stream.Size;
                    if (size == 0) { failCount++; await Task.Delay(100, ct); continue; }

                    stream.Seek(0);
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    await reader.LoadAsync(size);
                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);

                    failCount = 0; // 成功，重置失败计数
                    var origSrc = JpegToBitmapSource(bytes);
                    // 已在 UI 线程，直接赋值
                    OriginalFrameImage = origSrc;

                    var b64 = Convert.ToBase64String(bytes);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await _client.ProtectFrameAsync(OnlineUserName, b64);
                            if (string.IsNullOrEmpty(result.ProtectedFrameBase64)) return;
                            var protBytes = Convert.FromBase64String(result.ProtectedFrameBase64);
                            var protSrc   = JpegToBitmapSource(protBytes);
                            await _dispatcher.InvokeAsync(() =>
                            {
                                ProtectedFrameImage = protSrc;
                                OnlineLatencyText   = $"延迟：{result.LatencyMs:F0}ms";
                                // 预览窗口通过数据绑定自动更新，无需手动推帧
                            });
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "ProtectFrame"); }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    failCount++;
                    var msg = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
                    await _dispatcher.InvokeAsync(() =>
                        PreviewStatusText = $"WinRT 帧错误({failCount}): {msg}");

                    if (failCount >= 5)
                    {
                        // 连续 5 次失败，降级到 OpenCvSharp
                        await _dispatcher.InvokeAsync(() =>
                            PreviewStatusText = "WinRT 照片模式失败，降级到 DirectShow...");
                        mc.Dispose(); mc = null;
                        _ = Task.Run(CameraWorkerFallback);
                        return;
                    }
                    await Task.Delay(200);
                    continue;
                }

                // 维持约 5fps（减去已耗时间）
                var delay = Math.Max(10, 200 - (int)sw.ElapsedMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
        catch (UnauthorizedAccessException)
        {
            await _dispatcher.InvokeAsync(() =>
                PreviewStatusText = "摄像头：权限拒绝，请在 Windows 设置 → 隐私 → 相机中开启");
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
            await _dispatcher.InvokeAsync(() =>
            {
                PreviewStatusText = $"WinRT 初始化失败 (0x{ex.HResult:X8})：{msg}";
                OnlineStatusColor = "#CC3333";
            });
            // WinRT 彻底无法初始化时，降级到 OpenCvSharp
            _ = Task.Run(CameraWorkerFallback);
        }
        finally
        {
            mc?.Dispose();
        }
    }

    /// <summary>
    /// 降级方案：OpenCvSharp DirectShow/Any/MSMF 三后端依次尝试。
    /// 仅在 WinRT 照片模式连续失败时自动调用。
    /// </summary>
    private void CameraWorkerFallback()
    {
        var backends = new[]
        {
            ((VideoCaptureAPIs)700, "DirectShow"),
            ((VideoCaptureAPIs)0,   "Any"),
            (VideoCaptureAPIs.MSMF, "MSMF"),
        };

        VideoCapture? cap = null;
        foreach (var (api, name) in backends)
        {
            _dispatcher.InvokeAsync(() => PreviewStatusText = $"[降级] 摄像头：尝试 {name}...");
            try
            {
                var c = new VideoCapture(OnlineCameraIndex, api);
                c.Set(VideoCaptureProperties.ConvertRgb, 1.0);
                c.Set(VideoCaptureProperties.FrameWidth, 640);
                c.Set(VideoCaptureProperties.FrameHeight, 480);
                Thread.Sleep(800);
                if (!c.IsOpened()) { c.Dispose(); continue; }

                using var probe = new Mat();
                bool valid = false;
                for (int i = 0; i < 20 && _cameraCts?.IsCancellationRequested == false; i++)
                {
                    if (!c.Read(probe) || probe.Empty()) { Thread.Sleep(100); continue; }
                    var s = Cv2.Sum(probe);
                    double brightness = (s.Val0 + s.Val1 + s.Val2) / (probe.Rows * probe.Cols + 1.0);
                    _dispatcher.InvokeAsync(() =>
                        PreviewStatusText = $"[降级/{name}] 热身帧 {i + 1}/20 亮度={brightness:F1}");
                    if (brightness > 1.0) { valid = true; break; }
                    Thread.Sleep(100);
                }
                if (!valid) { c.Dispose(); continue; }

                cap = c;
                _dispatcher.InvokeAsync(() => PreviewStatusText = $"[降级] {name} 采集中...");
                break;
            }
            catch { }
        }

        if (cap == null)
        {
            _dispatcher.InvokeAsync(() =>
            {
                PreviewStatusText = "所有摄像头方案均失败，请勾选「屏幕区域捕获模式」";
                OnlineStatusColor = "#CC3333";
            });
            return;
        }

        _capture = cap;
        using var frameMat = new Mat();
        while (_cameraCts?.IsCancellationRequested == false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!cap.Read(frameMat) || frameMat.Empty()) { Thread.Sleep(100); continue; }

            Cv2.ImEncode(".jpg", frameMat, out var jpeg,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
            var origSrc = JpegToBitmapSource(jpeg);
            _dispatcher.InvokeAsync(() => OriginalFrameImage = origSrc);

            var b64 = Convert.ToBase64String(jpeg);
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _client.ProtectFrameAsync(OnlineUserName, b64);
                    if (string.IsNullOrEmpty(result.ProtectedFrameBase64)) return;
                    var protSrc = JpegToBitmapSource(Convert.FromBase64String(result.ProtectedFrameBase64));
                    await _dispatcher.InvokeAsync(() =>
                    {
                        ProtectedFrameImage = protSrc;
                        OnlineLatencyText   = $"延迟：{result.LatencyMs:F0}ms";
                        // 预览窗口通过数据绑定自动更新，无需手动推帧
                    });
                }
                catch { }
            });

            Thread.Sleep(Math.Max(10, 200 - (int)sw.ElapsedMilliseconds));
        }
        _capture?.Dispose();
        _capture = null;
    }

    // ─── 工具方法 ─────────────────────────────────────────────────────────

    /// <summary>将 JPEG 字节数组解码为可跨线程使用的 WPF BitmapSource。</summary>
    private static BitmapSource JpegToBitmapSource(byte[] jpegBytes)
    {
        using var ms = new MemoryStream(jpegBytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ─── 生命周期 ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        _generatePollTimer.Stop();
        _onlineStatusTimer.Stop();
        StopCameraLoop();
        _client.Dispose();
    }
}
