using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingAgent.Audio;

/// <summary>
/// 混合音频采集：系统环回（耳机/扬声器播放声）+ 可选麦克风。
/// 会议软件常把通话路由到「通信设备」，因此环回优先 Communications 角色。
/// </summary>
public sealed class HybridAudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<HybridAudioCaptureService> _logger;
    private readonly AudioOptions _options;
    private WasapiLoopbackCapture? _loopback;
    private WasapiCapture? _microphone;
    private BufferedWaveProvider? _loopbackBuffer;
    private BufferedWaveProvider? _microphoneBuffer;
    private long _chunkId;
    private string _activeDeviceDesc = "";
    private int _silentChunkCount;

    private readonly object _mixLock = new();
    private byte[] _pendingLoopback = Array.Empty<byte>();
    private byte[] _pendingMicrophone = Array.Empty<byte>();

    public HybridAudioCaptureService(
        ILogger<HybridAudioCaptureService> logger,
        IOptions<AudioOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public event EventHandler<AudioPacket>? AudioReady;
    public event EventHandler<string>? CaptureError;

    public bool IsRunning { get; private set; }
    public string ActiveDeviceDescription => _activeDeviceDesc;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning || !_options.Enabled)
        {
            return Task.CompletedTask;
        }

        var mode = NormalizeMode(_options.CaptureMode);
        var started = false;
        var errors = new List<string>();

        if (mode is "loopback" or "mixed")
        {
            if (TryStartLoopback(out var loopErr))
            {
                started = true;
            }
            else if (!string.IsNullOrEmpty(loopErr))
            {
                errors.Add(loopErr);
            }
        }

        if (mode is "microphone" or "mixed")
        {
            if (TryStartMicrophone(out var micErr))
            {
                started = true;
            }
            else if (!string.IsNullOrEmpty(micErr))
            {
                errors.Add(micErr);
            }
        }

        if (!started)
        {
            var msg = errors.Count > 0
                ? $"音频采集启动失败：{string.Join("；", errors)}"
                : "音频采集启动失败：无可用音频设备";
            _logger.LogError("{Message}", msg);
            CaptureError?.Invoke(this, msg);
            return Task.CompletedTask;
        }

        IsRunning = true;
        _silentChunkCount = 0;
        _logger.LogInformation("Audio capture started. Mode={Mode}, Devices={Devices}", mode, _activeDeviceDesc);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return Task.CompletedTask;
        }

        _loopback?.StopRecording();
        _microphone?.StopRecording();
        Cleanup();
        IsRunning = false;
        _activeDeviceDesc = "";
        lock (_mixLock)
        {
            _pendingLoopback = Array.Empty<byte>();
            _pendingMicrophone = Array.Empty<byte>();
        }

        _logger.LogInformation("Audio capture stopped.");
        return Task.CompletedTask;
    }

    private bool TryStartLoopback(out string error)
    {
        error = "";
        foreach (var role in ResolveLoopbackRoles())
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                _loopback = new WasapiLoopbackCapture(device);
                _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(10),
                    DiscardOnBufferOverflow = true
                };

                _loopback.DataAvailable += OnLoopbackDataAvailable;
                _loopback.RecordingStopped += OnRecordingStopped;
                _loopback.StartRecording();

                AppendDeviceDesc($"环回[{role}] {device.FriendlyName}");
                _logger.LogInformation("Loopback capture on {Role}: {Name}", role, device.FriendlyName);
                return true;
            }
            catch (Exception ex)
            {
                error = $"环回({role})：{ex.Message}";
                _logger.LogWarning(ex, "Loopback start failed for role {Role}", role);
                CleanupLoopbackOnly();
            }
        }

        return false;
    }

    private bool TryStartMicrophone(out string error)
    {
        error = "";
        foreach (var role in new[] { Role.Communications, Role.Multimedia })
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                _microphone = new WasapiCapture(device);
                _microphoneBuffer = new BufferedWaveProvider(_microphone.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(10),
                    DiscardOnBufferOverflow = true
                };

                _microphone.DataAvailable += OnMicrophoneDataAvailable;
                _microphone.RecordingStopped += OnRecordingStopped;
                _microphone.StartRecording();

                AppendDeviceDesc($"麦克风[{role}] {device.FriendlyName}");
                _logger.LogInformation("Microphone capture on {Role}: {Name}", role, device.FriendlyName);
                return true;
            }
            catch (Exception ex)
            {
                error = $"麦克风({role})：{ex.Message}";
                _logger.LogWarning(ex, "Microphone start failed for role {Role}", role);
                CleanupMicrophoneOnly();
            }
        }

        return false;
    }

    private IEnumerable<Role> ResolveLoopbackRoles()
    {
        if (string.Equals(_options.LoopbackRole, "Multimedia", StringComparison.OrdinalIgnoreCase))
        {
            yield return Role.Multimedia;
            yield return Role.Communications;
            yield break;
        }

        // 默认：会议类应用优先 Communications（耳机/通话设备）
        yield return Role.Communications;
        yield return Role.Multimedia;
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_loopback is null || _loopbackBuffer is null || e.BytesRecorded <= 0)
        {
            return;
        }

        _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        DrainBuffer(_loopbackBuffer, _loopback.WaveFormat, isLoopback: true);
    }

    private void OnMicrophoneDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_microphone is null || _microphoneBuffer is null || e.BytesRecorded <= 0)
        {
            return;
        }

        _microphoneBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        DrainBuffer(_microphoneBuffer, _microphone.WaveFormat, isLoopback: false);
    }

    private void DrainBuffer(BufferedWaveProvider buffer, WaveFormat inputFormat, bool isLoopback)
    {
        var chunkBytes = CalculateInputChunkBytes(inputFormat);
        while (buffer.BufferedBytes >= chunkBytes)
        {
            var source = new byte[chunkBytes];
            _ = buffer.Read(source, 0, chunkBytes);
            var pcm16 = AudioPcmConverter.Convert(source, inputFormat, _options);
            if (pcm16.Length == 0)
            {
                continue;
            }

            EnqueueOrEmit(isLoopback, pcm16);
        }
    }

    private void EnqueueOrEmit(bool isLoopback, byte[] pcm16)
    {
        var mode = NormalizeMode(_options.CaptureMode);
        if (mode != "mixed")
        {
            EmitPacket(pcm16);
            return;
        }

        lock (_mixLock)
        {
            if (isLoopback)
            {
                _pendingLoopback = AppendBytes(_pendingLoopback, pcm16);
            }
            else
            {
                _pendingMicrophone = AppendBytes(_pendingMicrophone, pcm16);
            }

            var mixLen = Math.Min(_pendingLoopback.Length, _pendingMicrophone.Length);
            if (mixLen >= GetOutputChunkBytes() / 2)
            {
                var loopPart = TakeBytes(ref _pendingLoopback, mixLen);
                var micPart = TakeBytes(ref _pendingMicrophone, mixLen);
                EmitPacket(AudioPcmConverter.MixPcm16(loopPart, micPart));
            }
            else if (_pendingLoopback.Length >= GetOutputChunkBytes())
            {
                EmitPacket(TakeBytes(ref _pendingLoopback, GetOutputChunkBytes()));
            }
            else if (_pendingMicrophone.Length >= GetOutputChunkBytes())
            {
                EmitPacket(TakeBytes(ref _pendingMicrophone, GetOutputChunkBytes()));
            }
        }
    }

    private void EmitPacket(byte[] pcm16)
    {
        if (pcm16.Length == 0)
        {
            return;
        }

        var peak = AudioPcmConverter.PeakAmplitude(pcm16);
        if (peak < 0.0005)
        {
            _silentChunkCount++;
            if (_silentChunkCount == 50)
            {
                _logger.LogWarning(
                    "Audio capture running but signal near silence for ~5s. Device={Device}. " +
                    "Check Windows default output device matches where meeting audio plays.",
                    _activeDeviceDesc);
            }
        }
        else
        {
            _silentChunkCount = 0;
        }

        AudioReady?.Invoke(this, new AudioPacket(
            ChunkId: Interlocked.Increment(ref _chunkId),
            TimestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SampleRate: _options.SampleRate,
            Channels: _options.Channels,
            BitsPerSample: _options.BitsPerSample,
            Data: pcm16));
    }

    private int GetOutputChunkBytes()
        => Math.Max(2, _options.SampleRate * _options.Channels * (_options.BitsPerSample / 8) * _options.ChunkMilliseconds / 1000);

    private int CalculateInputChunkBytes(WaveFormat inputFormat)
    {
        var bytes = inputFormat.AverageBytesPerSecond * _options.ChunkMilliseconds / 1000;
        return Math.Max(inputFormat.BlockAlign, bytes);
    }

    private static byte[] AppendBytes(byte[] existing, byte[] add)
    {
        if (existing.Length == 0)
        {
            return add;
        }

        var merged = new byte[existing.Length + add.Length];
        Buffer.BlockCopy(existing, 0, merged, 0, existing.Length);
        Buffer.BlockCopy(add, 0, merged, existing.Length, add.Length);
        return merged;
    }

    private static byte[] TakeBytes(ref byte[] buffer, int count)
    {
        if (buffer.Length <= count)
        {
            var all = buffer;
            buffer = Array.Empty<byte>();
            return all;
        }

        var taken = new byte[count];
        Buffer.BlockCopy(buffer, 0, taken, 0, count);
        var rest = new byte[buffer.Length - count];
        Buffer.BlockCopy(buffer, count, rest, 0, rest.Length);
        buffer = rest;
        return taken;
    }

    private void AppendDeviceDesc(string desc)
    {
        _activeDeviceDesc = string.IsNullOrEmpty(_activeDeviceDesc) ? desc : $"{_activeDeviceDesc} + {desc}";
    }

    private static string NormalizeMode(string? mode)
    {
        var m = (mode ?? "mixed").Trim().ToLowerInvariant();
        return m is "loopback" or "microphone" or "mixed" ? m : "mixed";
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        _logger.LogError(e.Exception, "Audio capture stopped with error.");
        CaptureError?.Invoke(this, $"音频采集中断: {e.Exception.Message}");
    }

    private void Cleanup()
    {
        CleanupLoopbackOnly();
        CleanupMicrophoneOnly();
    }

    private void CleanupLoopbackOnly()
    {
        if (_loopback is not null)
        {
            _loopback.DataAvailable -= OnLoopbackDataAvailable;
            _loopback.RecordingStopped -= OnRecordingStopped;
            _loopback.Dispose();
            _loopback = null;
        }

        _loopbackBuffer = null;
    }

    private void CleanupMicrophoneOnly()
    {
        if (_microphone is not null)
        {
            _microphone.DataAvailable -= OnMicrophoneDataAvailable;
            _microphone.RecordingStopped -= OnRecordingStopped;
            _microphone.Dispose();
            _microphone = null;
        }

        _microphoneBuffer = null;
    }
}
