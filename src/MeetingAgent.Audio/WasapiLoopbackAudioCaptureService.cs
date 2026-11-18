using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAgent.Audio;

public sealed class WasapiLoopbackAudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<WasapiLoopbackAudioCaptureService> _logger;
    private readonly AudioOptions _options;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private long _chunkId;

    public WasapiLoopbackAudioCaptureService(
        ILogger<WasapiLoopbackAudioCaptureService> logger,
        IOptions<AudioOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public event EventHandler<AudioPacket>? AudioReady;
    public event EventHandler<string>? CaptureError;

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning || !_options.Enabled)
        {
            return Task.CompletedTask;
        }

        try
        {
            _capture = new WasapiLoopbackCapture();
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            IsRunning = true;
            _logger.LogInformation("WASAPI loopback capture started.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start loopback capture");
            CaptureError?.Invoke(this, $"音频采集启动失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return Task.CompletedTask;
        }

        _capture?.StopRecording();
        Cleanup();
        IsRunning = false;
        _logger.LogInformation("WASAPI loopback capture stopped.");
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null || _buffer is null)
        {
            return;
        }

        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        var chunkBytes = CalculateInputChunkBytes(_capture.WaveFormat);
        while (_buffer.BufferedBytes >= chunkBytes)
        {
            var source = new byte[chunkBytes];
            _ = _buffer.Read(source, 0, chunkBytes);
            var pcm16 = ConvertToConfiguredPcm(source, _capture.WaveFormat);
            var now = DateTimeOffset.UtcNow;

            AudioReady?.Invoke(this, new AudioPacket(
                ChunkId: Interlocked.Increment(ref _chunkId),
                TimestampUtcMs: now.ToUnixTimeMilliseconds(),
                SampleRate: _options.SampleRate,
                Channels: _options.Channels,
                BitsPerSample: _options.BitsPerSample,
                Data: pcm16));
        }
    }

    private byte[] ConvertToConfiguredPcm(byte[] sourceBytes, WaveFormat inputFormat)
    {
        using var sourceStream = new RawSourceWaveStream(sourceBytes, 0, sourceBytes.Length, inputFormat);
        ISampleProvider sampleProvider = new WaveToSampleProvider(sourceStream);

        if (inputFormat.SampleRate != _options.SampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, _options.SampleRate);
        }

        if (inputFormat.Channels != _options.Channels)
        {
            if (_options.Channels == 1)
            {
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
            }
            else if (_options.Channels == 2 && inputFormat.Channels == 1)
            {
                sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
            }
        }

        var sampleCount = (int)((_options.SampleRate * _options.ChunkMilliseconds / 1000.0) * _options.Channels);
        var floatBuffer = new float[sampleCount];
        var read = sampleProvider.Read(floatBuffer, 0, sampleCount);

        var pcmBytes = new byte[read * 2];
        for (var i = 0; i < read; i++)
        {
            var clamped = Math.Clamp(floatBuffer[i], -1f, 1f);
            var sample = (short)(clamped * short.MaxValue);
            pcmBytes[i * 2] = (byte)(sample & 0xff);
            pcmBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }

        return pcmBytes;
    }

    private int CalculateInputChunkBytes(WaveFormat inputFormat)
    {
        var bytes = inputFormat.AverageBytesPerSecond * _options.ChunkMilliseconds / 1000;
        return Math.Max(inputFormat.BlockAlign, bytes);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        _logger.LogError(e.Exception, "WASAPI loopback capture failed.");
        CaptureError?.Invoke(this, $"音频采集失败: {e.Exception.Message}");
    }

    private void Cleanup()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _buffer = null;
    }
}
