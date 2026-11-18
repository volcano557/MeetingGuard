using MeetingAgent.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAgent.Audio;

/// <summary>将 WASAPI 原始 PCM / IEEE Float 转为配置采样率的 16-bit PCM。</summary>
internal static class AudioPcmConverter
{
    public static byte[] Convert(byte[] sourceBytes, WaveFormat inputFormat, AudioOptions options)
    {
        if (sourceBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using var sourceStream = new RawSourceWaveStream(sourceBytes, 0, sourceBytes.Length, inputFormat);
        ISampleProvider sampleProvider = sourceStream.ToSampleProvider();

        if (inputFormat.SampleRate != options.SampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, options.SampleRate);
        }

        if (inputFormat.Channels != options.Channels)
        {
            if (options.Channels == 1 && inputFormat.Channels >= 2)
            {
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
            }
            else if (options.Channels == 2 && inputFormat.Channels == 1)
            {
                sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
            }
        }

        // 读完整个输入块，避免重采样器只输出部分样本导致“有播放无采集”
        var all = new List<float>(sourceBytes.Length / 4);
        var buf = new float[4096];
        int read;
        while ((read = sampleProvider.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                all.Add(buf[i]);
            }
        }

        if (all.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var pcmBytes = new byte[all.Count * 2];
        for (var i = 0; i < all.Count; i++)
        {
            var clamped = Math.Clamp(all[i], -1f, 1f);
            var sample = (short)(clamped * short.MaxValue);
            pcmBytes[i * 2] = (byte)(sample & 0xff);
            pcmBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }

        return pcmBytes;
    }

    public static double PeakAmplitude(byte[] pcm16)
    {
        if (pcm16.Length < 2)
        {
            return 0;
        }

        double peak = 0;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            var amp = Math.Abs(sample / 32768.0);
            if (amp > peak)
            {
                peak = amp;
            }
        }

        return peak;
    }

    /// <summary>将两路同采样率 PCM16 单声道样本按样本相加混合（限幅）。</summary>
    public static byte[] MixPcm16(byte[] a, byte[] b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;

        var len = Math.Min(a.Length, b.Length);
        if (len % 2 != 0)
        {
            len--;
        }

        var mixed = new byte[len];
        for (var i = 0; i + 1 < len; i += 2)
        {
            var sa = (short)(a[i] | (a[i + 1] << 8));
            var sb = (short)(b[i] | (b[i + 1] << 8));
            var v = Math.Clamp(sa + sb, short.MinValue, short.MaxValue);
            mixed[i] = (byte)(v & 0xff);
            mixed[i + 1] = (byte)((v >> 8) & 0xff);
        }

        return mixed;
    }
}
