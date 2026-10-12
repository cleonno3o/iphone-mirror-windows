using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace IPhoneMirror
{
    internal sealed class VideoGeometry
    {
        internal readonly int Width, Height;
        internal readonly double Aspect;
        private VideoGeometry(int width, int height, double aspect)
        { Width = width; Height = height; Aspect = aspect; }

        // Only negotiated sink caps describe the displayed video. Ignore decoder
        // capability lists, which may contain ranges or unrelated resolutions.
        internal static VideoGeometry Parse(string line)
        {
            if (line == null || !line.Contains("d3d11videosink") || !line.Contains("set caps video/x-raw")) return null;
            int width = ReadInteger(line, @"\bwidth=\(int\)(\d+)");
            int height = ReadInteger(line, @"\bheight=\(int\)(\d+)");
            if (width < 1 || height < 1 || width > 16384 || height > 16384) return null;
            double pixelAspect = 1;
            if (line.Contains("pixel-aspect-ratio="))
            {
                var match = Regex.Match(line, @"pixel-aspect-ratio=\(fraction\)(\d+)/(\d+)");
                int numerator, denominator;
                if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out numerator) ||
                    !Int32.TryParse(match.Groups[2].Value, out denominator) ||
                    numerator < 1 || denominator < 1 || numerator > 65536 || denominator > 65536) return null;
                pixelAspect = (double)numerator / denominator;
            }
            double aspect = width * pixelAspect / height;
            if (aspect < 0.1 || aspect > 10) return null;
            return new VideoGeometry(width, height, aspect);
        }

        private static int ReadInteger(string line, string pattern)
        {
            var match = Regex.Match(line, pattern);
            int value;
            return match.Success && Int32.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rectangle
        {
            internal int Left, Top, Right, Bottom;
            internal int Width { get { return Right - Left; } }
            internal int Height { get { return Bottom - Top; } }
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            internal int Size;
            internal Rectangle Monitor, Work;
            internal uint Flags;
        }
        internal delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window, string text);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rectangle rect);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out Rectangle rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        internal static IntPtr FindVideoWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                uint owner;
                GetWindowThreadProcessId(handle, out owner);
                if (owner != processId || !IsWindowVisible(handle)) return true;
                var title = new StringBuilder(512);
                GetWindowText(handle, title, title.Capacity);
                if (title.ToString().IndexOf("renderer", StringComparison.OrdinalIgnoreCase) < 0 && title.ToString() != "iPhone 화면") return true;
                SetWindowText(handle, "iPhone 화면");
                found = handle;
                return false;
            }, IntPtr.Zero);
            return found;
        }
    }

    internal sealed class VideoWindowFollower
    {
        private IntPtr previousWindow;
        private double appliedAspect;

        internal void Reset() { previousWindow = IntPtr.Zero; appliedAspect = 0; }

        internal bool Update(IntPtr window, VideoGeometry video)
        {
            if (window != previousWindow) { previousWindow = window; appliedAspect = 0; }
            if (window == IntPtr.Zero || video == null || !Native.IsWindowVisible(window)) return false;
            // Leave manual sizing alone until the video aspect actually changes.
            if (Math.Abs(appliedAspect - video.Aspect) < 0.005) return false;
            // Defer rotation while minimized, maximized, or borderless fullscreen.
            // Do not mark it applied: restoring the window must apply the latest ratio.
            if (Native.IsIconic(window) || Native.IsZoomed(window) ||
                (Native.GetWindowStyle(window, -16) & 0x00C00000) != 0x00C00000) return false;

            Native.Rectangle outer, client;
            if (!Native.GetWindowRect(window, out outer) || !Native.GetClientRect(window, out client) ||
                client.Width <= 0 || client.Height <= 0) return false;
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor)) return false;
            int frameWidth = outer.Width - client.Width, frameHeight = outer.Height - client.Height;
            int availableWidth = monitor.Work.Width - frameWidth - 24;
            int availableHeight = monitor.Work.Height - frameHeight - 24;
            if (availableWidth <= 0 || availableHeight <= 0) return false;

            // Keep the current long edge when turning, then fit the active monitor.
            double longEdge = Math.Max(client.Width, client.Height);
            double width = video.Aspect >= 1 ? longEdge : longEdge * video.Aspect;
            double height = video.Aspect >= 1 ? longEdge / video.Aspect : longEdge;
            double scale = Math.Min(1, Math.Min(availableWidth / width, availableHeight / height));
            int targetWidth = (int)Math.Round(width * scale) + frameWidth;
            int targetHeight = (int)Math.Round(height * scale) + frameHeight;
            int x = outer.Left + (outer.Width - targetWidth) / 2;
            int y = outer.Top + (outer.Height - targetHeight) / 2;
            x = Math.Max(monitor.Work.Left + 12, Math.Min(x, monitor.Work.Right - targetWidth - 12));
            y = Math.Max(monitor.Work.Top + 12, Math.Min(y, monitor.Work.Bottom - targetHeight - 12));
            // ASYNCWINDOWPOS avoids blocking the WPF UI on a receiver window thread.
            if (!Native.SetWindowPos(window, IntPtr.Zero, x, y, targetWidth, targetHeight, 0x4014)) return false;
            appliedAspect = video.Aspect;
            return true;
        }
    }
}
