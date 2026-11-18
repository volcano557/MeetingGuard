using K4os.Compression.LZ4;
using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingAgent.Transport;

public sealed class LosslessFrameCompressionService : IFrameCompressionService
{
    private static readonly HashSet<string> SupportedLosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "lz4"
    };

    private static readonly HashSet<string> BlockedLossyCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpeg",
        "jpg",
        "h264",
        "h265",
        "hevc",
        "webp-lossy",
        "webp"
    };

    private readonly IOptionsMonitor<LinuxBackendOptions> _optionsMonitor;
    private readonly ILogger<LosslessFrameCompressionService> _logger;

    public LosslessFrameCompressionService(
        IOptionsMonitor<LinuxBackendOptions> optionsMonitor,
        ILogger<LosslessFrameCompressionService> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public FramePacket CompressFrame(FramePacket framePacket)
    {
        var options = _optionsMonitor.CurrentValue;
        var compression = (options.Compression ?? "none").Trim().ToLowerInvariant();

        if (BlockedLossyCodecs.Contains(compression))
        {
            throw new InvalidOperationException(
                $"检测链路禁止使用有损压缩算法: {compression}. 仅允许 none 或无损算法。");
        }

        if (!options.UseLosslessCompression || string.Equals(compression, "none", StringComparison.OrdinalIgnoreCase))
        {
            return framePacket with
            {
                IsLosslessCompressed = false,
                Compression = "none"
            };
        }

        if (!SupportedLosslessCodecs.Contains(compression))
        {
            throw new InvalidOperationException(
                $"不支持的压缩算法: {compression}. 当前仅支持 none / lz4。");
        }

        if (string.Equals(compression, "lz4", StringComparison.OrdinalIgnoreCase))
        {
            var source = framePacket.Data;
            var maxTargetLength = LZ4Codec.MaximumOutputSize(source.Length);
            var target = new byte[maxTargetLength];
            var encodedLength = LZ4Codec.Encode(source, 0, source.Length, target, 0, target.Length);
            if (encodedLength <= 0)
            {
                _logger.LogWarning("LZ4 encode returned invalid length, fallback to raw frame. frame_id={FrameId}",
                    framePacket.FrameId);
                return framePacket with
                {
                    IsLosslessCompressed = false,
                    Compression = "none"
                };
            }

            var encoded = new byte[encodedLength];
            Buffer.BlockCopy(target, 0, encoded, 0, encodedLength);
            return framePacket with
            {
                IsLosslessCompressed = true,
                Compression = "lz4",
                Data = encoded
            };
        }

        return framePacket with
        {
            IsLosslessCompressed = false,
            Compression = "none"
        };
    }
}
