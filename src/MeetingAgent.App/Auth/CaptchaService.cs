using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeetingAgent.App.Auth;

/// <summary>
/// 图片验证码服务：生成 4 位随机字符 + 干扰线 + 噪点，用于登录 / 注册校验。
/// 对齐功能分析.pdf：身份认证模块 + 图片验证码（防机器人爆破）。
/// </summary>
public class CaptchaService
{
    // 排除易混淆字符：0 / O / 1 / I / l
    private const string CharSet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private const int CanvasWidth  = 160;
    private const int CanvasHeight = 50;

    private string _currentCode = string.Empty;

    /// <summary>生成新验证码并返回可直接放进 Image.Source 的 BitmapSource。</summary>
    public (string Code, ImageSource Image) Generate()
    {
        _currentCode = GenerateRandomCode(4);
        var img = Render(_currentCode);
        return (_currentCode, img);
    }

    /// <summary>不区分大小写校验用户输入是否正确。</summary>
    public bool Verify(string userInput) =>
        !string.IsNullOrEmpty(_currentCode) &&
        string.Equals(userInput?.Trim(), _currentCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>当前验证码（仅用于刷新等需要重新比对的场景）。</summary>
    public string CurrentCode => _currentCode;

    // ─── 内部实现 ─────────────────────────────────────────────

    private static string GenerateRandomCode(int len)
    {
        var bytes = RandomNumberGenerator.GetBytes(len);
        Span<char> buf = stackalloc char[len];
        for (int i = 0; i < len; i++)
            buf[i] = CharSet[bytes[i] % CharSet.Length];
        return new string(buf);
    }

    private static BitmapSource Render(string code)
    {
        var rnd = new Random();
        var canvas = new Canvas
        {
            Width  = CanvasWidth,
            Height = CanvasHeight,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)),
        };

        // 干扰线：3–5 条彩色斜线
        var colors = new[]
        {
            Color.FromRgb(0x58, 0xA6, 0xFF),
            Color.FromRgb(0x3F, 0xB9, 0x50),
            Color.FromRgb(0xF6, 0xC9, 0x5F),
            Color.FromRgb(0xA9, 0x8B, 0xFF),
            Color.FromRgb(0xF8, 0x51, 0x49),
        };
        int lineCount = rnd.Next(3, 6);
        for (int i = 0; i < lineCount; i++)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = rnd.NextDouble() * CanvasWidth,
                Y1 = rnd.NextDouble() * CanvasHeight,
                X2 = rnd.NextDouble() * CanvasWidth,
                Y2 = rnd.NextDouble() * CanvasHeight,
                Stroke = new SolidColorBrush(colors[rnd.Next(colors.Length)]) { Opacity = 0.55 },
                StrokeThickness = 1,
            };
            canvas.Children.Add(line);
        }

        // 噪点：散布的小椭圆
        for (int i = 0; i < 28; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width  = rnd.Next(1, 3),
                Height = rnd.Next(1, 3),
                Fill   = new SolidColorBrush(colors[rnd.Next(colors.Length)]) { Opacity = 0.5 },
            };
            Canvas.SetLeft(dot, rnd.NextDouble() * CanvasWidth);
            Canvas.SetTop(dot,  rnd.NextDouble() * CanvasHeight);
            canvas.Children.Add(dot);
        }

        // 4 个字符：各自带随机旋转角度 / 字体颜色
        // 左右各留 14 px 安全区，避免被容器裁切或旋转后越界
        const double padX = 14;
        double slotW = (CanvasWidth - padX * 2) / (double)code.Length;
        for (int i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            var tb = new TextBlock
            {
                Text       = ch.ToString(),
                FontSize   = 26,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(colors[rnd.Next(colors.Length)]),
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            };
            tb.RenderTransformOrigin = new Point(0.5, 0.5);
            tb.RenderTransform = new RotateTransform(rnd.NextDouble() * 24 - 12); // ±12°，更稳
            // 把字符中心落在 slot 中央，避免溢出
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cx = padX + slotW * (i + 0.5);
            double cy = CanvasHeight / 2.0;
            Canvas.SetLeft(tb, cx - tb.DesiredSize.Width  / 2 + (rnd.NextDouble() * 2 - 1));
            Canvas.SetTop (tb, cy - tb.DesiredSize.Height / 2 + (rnd.NextDouble() * 2 - 1));
            canvas.Children.Add(tb);
        }

        // 把 Canvas 渲染到位图
        canvas.Measure(new Size(CanvasWidth, CanvasHeight));
        canvas.Arrange(new Rect(0, 0, CanvasWidth, CanvasHeight));
        var bmp = new RenderTargetBitmap(CanvasWidth, CanvasHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(canvas);
        bmp.Freeze();
        return bmp;
    }
}
