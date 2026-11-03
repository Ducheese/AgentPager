using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace AgentPager.Bridge;

/// <summary>
/// 桌面屏幕截图服务：优先系统主显示器（无主屏时回退枚举首项），保持原始像素尺寸，按 JPEG Q85 编码。
/// DPI 由 Windows 系统设置决定，不做手工缩放。
/// </summary>
public static class DesktopCapture
{
    private const long JpegQuality = 85L;

    /// <summary>
    /// 截取主显示器的画面并返回 JPEG 字节数组。保持原始分辨率，固定 Q85，不限制文件大小；
    /// 失败（如锁屏或无活动显示器）返回 null。
    /// </summary>
    public static byte[]? CaptureScreenZeroJpeg()
    {
        try
        {
            var bounds = GetScreenZeroBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            return EncodeJpeg(bitmap);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodeJpeg(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        var encoder = GetEncoder(ImageFormat.Jpeg)
            ?? throw new InvalidOperationException("Windows 未提供 JPEG 编码器");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        bitmap.Save(ms, encoder, parameters);
        return ms.ToArray();
    }

    private static Rectangle GetScreenZeroBounds()
    {
        // 优先系统主显示器；只有主屏不可用时才回退到枚举首项。
        // 进程按 PerMonitorV2 启动，Bounds 就是该显示器的物理像素；DPI 取系统设置，不手工换算。
        var screen = Screen.PrimaryScreen;
        if (screen is null && Screen.AllScreens.Length > 0)
        {
            screen = Screen.AllScreens[0];
        }

        if (screen is null)
        {
            return Rectangle.Empty;
        }

        var bounds = screen.Bounds;
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : Rectangle.Empty;
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        return Array.Find(
            ImageCodecInfo.GetImageEncoders(),
            codec => codec.FormatID == format.Guid);
    }
}
