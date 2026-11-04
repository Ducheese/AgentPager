using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AgentPager.Bridge;

public static class DesktopInput
{
    private static readonly object Gate = new();

    private const uint InputMouse = 0;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    public static bool ClickNormalized(double normalizedX, double normalizedY, string? action, out string? error)
    {
        error = null;
        if (!IsNormalized(normalizedX) || !IsNormalized(normalizedY))
        {
            error = "点击坐标必须在 0..1 之间";
            return false;
        }

        var bounds = DesktopCapture.GetScreenZeroBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            error = "没有可用的主显示器";
            return false;
        }
        var target = ScreenPointForNormalized(bounds, normalizedX, normalizedY);

        lock (Gate)
        {
            var inputDesktop = OpenInputDesktop(0, false, DesktopReadObjects | DesktopWriteObjects);
            if (inputDesktop == IntPtr.Zero)
            {
                error = "当前输入桌面不可用（锁屏或安全桌面）";
                return false;
            }
            try
            {
                var virtualScreen = SystemInformation.VirtualScreen;
                if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
                {
                    error = "没有可用的虚拟桌面";
                    return false;
                }

                var absoluteX = (int)Math.Round(
                    (target.X - virtualScreen.Left) * 65535.0 / Math.Max(1, virtualScreen.Width - 1));
                var absoluteY = (int)Math.Round(
                    (target.Y - virtualScreen.Top) * 65535.0 / Math.Max(1, virtualScreen.Height - 1));
                var move = MouseInput(MouseMove | MouseAbsolute | MouseVirtualDesk, absoluteX, absoluteY);
                if (SendInput(1, new[] { move }, Marshal.SizeOf<INPUT>()) != 1)
                {
                    error = $"移动鼠标失败 (Win32 {Marshal.GetLastWin32Error()})";
                    return false;
                }

                var deadline = Environment.TickCount64 + 200;
                while (true)
                {
                    if (GetCursorPos(out var actual) &&
                        Math.Abs(actual.X - target.X) <= 1 &&
                        Math.Abs(actual.Y - target.Y) <= 1)
                    {
                        break;
                    }
                    if (Environment.TickCount64 >= deadline)
                    {
                        // 浮点取整容差兜底：若距离目标在 3px 范围内，用 SetCursorPos 强行补正，绝不误杀正常点击
                        if (GetCursorPos(out actual) &&
                            Math.Abs(actual.X - target.X) <= 3 &&
                            Math.Abs(actual.Y - target.Y) <= 3)
                        {
                            SetCursorPos(target.X, target.Y);
                            break;
                        }
                        error = "鼠标未能移动到目标位置";
                        return false;
                    }
                    Thread.Sleep(5);
                }

                var act = (action ?? "left").Trim().ToLowerInvariant();
                if (act != "left" && act != "right" && act != "double")
                {
                    act = "left";
                }

                INPUT[] clicks;
                if (act == "right")
                {
                    clicks = new[]
                    {
                        MouseInput(MouseRightDown),
                        MouseInput(MouseRightUp),
                    };
                }
                else
                {
                    clicks = new[]
                    {
                        MouseInput(MouseLeftDown),
                        MouseInput(MouseLeftUp),
                    };
                }

                var sent = SendInput((uint)clicks.Length, clicks, Marshal.SizeOf<INPUT>());
                if (sent != (uint)clicks.Length)
                {
                    SendInput(1, new[] { MouseInput(act == "right" ? MouseRightUp : MouseLeftUp) }, Marshal.SizeOf<INPUT>());
                    error = $"发送点击失败 (Win32 {Marshal.GetLastWin32Error()})";
                    return false;
                }

                if (act == "double")
                {
                    Thread.Sleep(45);
                    var secondSent = SendInput((uint)clicks.Length, clicks, Marshal.SizeOf<INPUT>());
                    if (secondSent != (uint)clicks.Length)
                    {
                        SendInput(1, new[] { MouseInput(MouseLeftUp) }, Marshal.SizeOf<INPUT>());
                        error = $"发送双击失败 (Win32 {Marshal.GetLastWin32Error()})";
                        return false;
                    }
                }
            }
            finally
            {
                CloseDesktop(inputDesktop);
            }
        }
        return true;
    }

    public static bool ClickNormalized(double normalizedX, double normalizedY, out string? error) =>
        ClickNormalized(normalizedX, normalizedY, "left", out error);

    public static Point ScreenPointForNormalized(Rectangle bounds, double normalizedX, double normalizedY)
    {
        var x = bounds.Left + (int)Math.Round(
            Math.Clamp(normalizedX, 0, 1) * (bounds.Width - 1));
        var y = bounds.Top + (int)Math.Round(
            Math.Clamp(normalizedY, 0, 1) * (bounds.Height - 1));
        return new Point(x, y);
    }

    private static bool IsNormalized(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value is >= 0 and <= 1;

    private static INPUT MouseInput(uint flags, int dx = 0, int dy = 0) => new()
    {
        type = InputMouse,
        mi = new MOUSEINPUT
        {
            dx = dx,
            dy = dy,
            dwFlags = flags,
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int X, int Y);

    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopWriteObjects = 0x0080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
