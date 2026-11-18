using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using static MeetingAgent.Capture.CaptureWin32Interop;

namespace MeetingAgent.Capture;

public sealed class HybridWindowsCaptureService : IVideoCaptureService
{
    private readonly ILogger<HybridWindowsCaptureService> _logger;
    private readonly CaptureOptions _options;
    private readonly object _sync = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _stagingTexture;
    private IDirect3DDevice? _winRtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private long _frameId;
    private long _lastFrameMs;
    private bool _pausedByMinimize;
    private bool _enableResizeForSession;
    private int _targetResizeWidth;
    private int _targetResizeHeight;
    private CancellationTokenSource? _gdiCaptureCts;
    private Task? _gdiCaptureTask;

    public HybridWindowsCaptureService(
        ILogger<HybridWindowsCaptureService> logger,
        IOptions<CaptureOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public event EventHandler<FramePacket>? FrameReady;
    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    public bool IsRunning { get; private set; }
    public WindowDescriptor? CurrentWindow { get; private set; }

    public Task StartAsync(WindowDescriptor target, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            try
            {
                _item = GraphicsCaptureItemFactory.CreateForWindow(target.Hwnd);
                _item.Closed += OnItemClosed;

                _device = D3D11.D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport);
                _context = _device.ImmediateContext;
                _winRtDevice = Direct3D11Interop.CreateDirect3DDevice(_device);

                var size = _item.Size;
                ConfigureResize(size.Width, size.Height);
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    size);
                _framePool.FrameArrived += OnFrameArrived;
                _session = _framePool.CreateCaptureSession(_item);
                _session.IsCursorCaptureEnabled = true;

                _lastFrameMs = 0;
                _frameId = 0;
                CurrentWindow = target;
                IsRunning = true;
                _session.StartCapture();
            }
            catch (Exception ex) when (ex is InvalidCastException or COMException)
            {
                _logger.LogWarning(ex, "WinRT capture init failed, fallback to GDI window capture.");
                DisposeResources();
                StartGdiCaptureFallback(target);
            }
        }

        _logger.LogInformation("Started Windows.Graphics.Capture on {Title}", target.Title);
        StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.Running, "Capture started"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!IsRunning)
            {
                return Task.CompletedTask;
            }

            _gdiCaptureCts?.Cancel();
            _gdiCaptureCts?.Dispose();
            _gdiCaptureCts = null;
            _gdiCaptureTask = null;
            DisposeResources();
            IsRunning = false;
            CurrentWindow = null;
        }

        _logger.LogInformation("Capture stopped");
        StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.Stopped, "Capture stopped"));
        return Task.CompletedTask;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!IsRunning)
        {
            return;
        }

        if (CurrentWindow is not null)
        {
            if (!IsWindow(CurrentWindow.Hwnd))
            {
                _logger.LogWarning("Capture target window is no longer valid.");
                _ = StopAsync(CancellationToken.None);
                StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.WindowClosed, "Window invalid"));
                return;
            }

            if (IsIconic(CurrentWindow.Hwnd))
            {
                if (!_pausedByMinimize)
                {
                    _pausedByMinimize = true;
                    StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.Paused, "Window minimized"));
                }
                return;
            }

            if (_pausedByMinimize)
            {
                _pausedByMinimize = false;
                StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.Running, "Window restored"));
            }
        }

        using var frame = sender.TryGetNextFrame();
        if (frame is null || _device is null || _context is null || _item is null)
        {
            return;
        }

        var fps = Math.Max(1, _options.TargetFps);
        var minIntervalMs = 1000 / fps;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _lastFrameMs < minIntervalMs)
        {
            return;
        }
        _lastFrameMs = nowMs;

        var contentSize = frame.ContentSize;
        EnsureStagingTexture(contentSize.Width, contentSize.Height);

        using var sourceTexture = Direct3D11Interop.CreateSharpDXTexture2D(frame.Surface);
        _context.CopyResource(_stagingTexture!, sourceTexture);
        var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        var sourceWidth = contentSize.Width;
        var sourceHeight = contentSize.Height;
        var sourceStride = sourceWidth * 4;
        var sourceBytes = new byte[sourceStride * sourceHeight];

        for (var row = 0; row < sourceHeight; row++)
        {
            var rowOffset = row * (int)mapped.RowPitch;
            var rowPtr = (nint)mapped.DataPointer + rowOffset;
            Marshal.Copy((IntPtr)rowPtr, sourceBytes, row * sourceStride, sourceStride);
        }

        _context.Unmap(_stagingTexture!, 0);

        var outputWidth = sourceWidth;
        var outputHeight = sourceHeight;
        var outputStride = sourceStride;
        var outputBytes = sourceBytes;

        if (_enableResizeForSession && _targetResizeWidth > 0 && _targetResizeHeight > 0)
        {
            outputWidth = _targetResizeWidth;
            outputHeight = _targetResizeHeight;
            outputStride = outputWidth * 4;
            outputBytes = ResizeBgraNearest(sourceBytes, sourceWidth, sourceHeight, outputWidth, outputHeight);
        }

        var packet = new FramePacket(
            Interlocked.Increment(ref _frameId),
            nowMs,
            outputWidth,
            outputHeight,
            PixelFormatKind.BGRA8,
            outputStride,
            IsLosslessCompressed: false,
            Compression: "none",
            Data: outputBytes);

        FrameReady?.Invoke(this, packet);
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_device is null)
        {
            return;
        }

        if (_stagingTexture is not null)
        {
            var desc = _stagingTexture.Description;
            if (desc.Width == width && desc.Height == height)
            {
                return;
            }

            _stagingTexture.Dispose();
            _stagingTexture = null;
        }

        var textureDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            BindFlags = BindFlags.None,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            SampleDescription = new SampleDescription(1, 0)
        };

        _stagingTexture = _device.CreateTexture2D(textureDesc);
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _logger.LogWarning("Capture window closed");
        _ = StopAsync(CancellationToken.None);
        StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.WindowClosed, "Window closed"));
    }

    private void DisposeResources()
    {
        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _stagingTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _item = null;
        _session = null;
        _framePool = null;
        _stagingTexture = null;
        _context = null;
        _device = null;
        _winRtDevice = null;
        _pausedByMinimize = false;
        _enableResizeForSession = false;
        _targetResizeWidth = 0;
        _targetResizeHeight = 0;
    }

    private void StartGdiCaptureFallback(WindowDescriptor target)
    {
        CurrentWindow = target;
        IsRunning = true;
        _lastFrameMs = 0;
        _frameId = 0;
        _gdiCaptureCts = new CancellationTokenSource();
        _gdiCaptureTask = Task.Run(() => GdiCaptureLoopAsync(_gdiCaptureCts.Token), _gdiCaptureCts.Token);
    }

    private async Task GdiCaptureLoopAsync(CancellationToken cancellationToken)
    {
        var fps = Math.Max(1, _options.TargetFps);
        var delay = TimeSpan.FromMilliseconds(Math.Max(1, 1000 / fps));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var window = CurrentWindow;
                if (window is null)
                {
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (!IsWindow(window.Hwnd))
                {
                    StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.WindowClosed, "Window invalid"));
                    await StopAsync(CancellationToken.None);
                    return;
                }

                if (IsIconic(window.Hwnd))
                {
                    if (!_pausedByMinimize)
                    {
                        _pausedByMinimize = true;
                        StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.Paused, "Window minimized"));
                    }
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (_pausedByMinimize)
                {
                    _pausedByMinimize = false;
                    StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureLifecycleState.Running, "Window restored"));
                }

                if (!GetWindowRect(window.Hwnd, out var rect))
                {
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var width = Math.Max(1, rect.Right - rect.Left);
                var height = Math.Max(1, rect.Bottom - rect.Top);
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var stride = Math.Abs(data.Stride);
                    var buffer = new byte[stride * height];
                    for (var row = 0; row < height; row++)
                    {
                        var sourcePtr = data.Scan0 + row * data.Stride;
                        Marshal.Copy(sourcePtr, buffer, row * stride, stride);
                    }

                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var packet = new FramePacket(
                        Interlocked.Increment(ref _frameId),
                        nowMs,
                        width,
                        height,
                        PixelFormatKind.BGRA8,
                        stride,
                        IsLosslessCompressed: false,
                        Compression: "none",
                        Data: buffer);
                    FrameReady?.Invoke(this, packet);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GDI capture loop failed.");
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private void ConfigureResize(int sourceWidth, int sourceHeight)
    {
        _enableResizeForSession = false;
        _targetResizeWidth = 0;
        _targetResizeHeight = 0;

        if (!_options.EnableResize || _options.ResizeWidth <= 0 || _options.ResizeHeight <= 0)
        {
            return;
        }

        var sourceRatio = (double)sourceWidth / sourceHeight;
        var targetRatio = (double)_options.ResizeWidth / _options.ResizeHeight;
        var ratioDelta = Math.Abs(sourceRatio - targetRatio);
        if (ratioDelta > 0.01)
        {
            _logger.LogWarning(
                "Resize is disabled because aspect ratio mismatch. source={SourceWidth}x{SourceHeight}, target={TargetWidth}x{TargetHeight}",
                sourceWidth, sourceHeight, _options.ResizeWidth, _options.ResizeHeight);
            StateChanged?.Invoke(this,
                new CaptureStateChangedEventArgs(
                    CaptureLifecycleState.Running,
                    "Resize disabled due to aspect ratio mismatch"));
            return;
        }

        _enableResizeForSession = true;
        _targetResizeWidth = _options.ResizeWidth;
        _targetResizeHeight = _options.ResizeHeight;
        _logger.LogWarning(
            "Resize is enabled by explicit configuration for bandwidth trade-off. target={TargetWidth}x{TargetHeight}",
            _targetResizeWidth, _targetResizeHeight);
    }

    private static byte[] ResizeBgraNearest(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var target = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        {
            var srcY = y * sourceHeight / targetHeight;
            for (var x = 0; x < targetWidth; x++)
            {
                var srcX = x * sourceWidth / targetWidth;
                var srcOffset = (srcY * sourceWidth + srcX) * 4;
                var dstOffset = (y * targetWidth + x) * 4;
                target[dstOffset] = source[srcOffset];
                target[dstOffset + 1] = source[srcOffset + 1];
                target[dstOffset + 2] = source[srcOffset + 2];
                target[dstOffset + 3] = source[srcOffset + 3];
            }
        }

        return target;
    }
}

internal static class CaptureWin32Interop
{
    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
