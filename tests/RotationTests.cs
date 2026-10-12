using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IPhoneMirror
{
    // Exercises the bundled sink in a child process, including real caps changes
    // and native window resizing. This executable is never part of the app build.
    internal static class RotationTests
    {
        private static volatile VideoGeometry current;
        private static readonly StringBuilder report = new StringBuilder();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("libgstreamer-1.0-0.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void gst_init(IntPtr argc, IntPtr argv);
        [DllImport("libgstreamer-1.0-0.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr gst_parse_launch(string text, out IntPtr error);
        [DllImport("libgstreamer-1.0-0.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int gst_element_set_state(IntPtr element, int state);
        [DllImport("libgstreamer-1.0-0.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr gst_bin_get_by_name(IntPtr bin, string name);
        [DllImport("libgstreamer-1.0-0.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void gst_util_set_object_arg(IntPtr obj, string property, string value);
        [DllImport("libgstreamer-1.0-0.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void gst_object_unref(IntPtr obj);

        private static int Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--video") return Video(args[1]);
            string root = args[0];
            Process child = null;
            int result = 0;
            try
            {
                Assert(VideoGeometry.Parse(Caps(360, 780)) != null, "valid portrait caps");
                Assert(VideoGeometry.Parse("width=(int)360,height=(int)780") == null, "unrelated diagnostics ignored");
                Assert(VideoGeometry.Parse(Caps(0, 780)) == null, "invalid dimensions ignored");
                Assert(VideoGeometry.Parse(Caps(360, 780) + ", pixel-aspect-ratio=(fraction)1/0") == null, "invalid pixel ratio ignored");
                Assert(Math.Abs(VideoGeometry.Parse(Caps(720, 576) + ", pixel-aspect-ratio=(fraction)16/15").Aspect - 4.0 / 3) < 0.001, "pixel aspect ratio applied");
                var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--video \"" + root + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = info.RedirectStandardError = true;
                string runtime = Path.Combine(root, "runtime", "app", "_internal");
                info.EnvironmentVariables["PATH"] = Path.Combine(runtime, "bin") + ";" + Environment.GetEnvironmentVariable("PATH");
                info.EnvironmentVariables["GST_PLUGIN_PATH"] = Path.Combine(runtime, "lib", "gstreamer-1.0");
                info.EnvironmentVariables["GST_PLUGIN_SYSTEM_PATH"] = info.EnvironmentVariables["GST_PLUGIN_PATH"];
                info.EnvironmentVariables["GST_REGISTRY"] = Path.Combine(root, "state", "gstreamer-registry.bin");
                info.EnvironmentVariables["GST_REGISTRY_FORK"] = "no";
                info.EnvironmentVariables["GST_DEBUG"] = "d3d11videosink:5";
                info.EnvironmentVariables["GST_DEBUG_NO_COLOR"] = "1";
                info.EnvironmentVariables.Remove("GST_DEBUG_FILE");
                child = new Process { StartInfo = info };
                child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    var geometry = VideoGeometry.Parse(e.Data);
                    if (geometry != null) current = geometry;
                };
                child.Start();
                child.BeginErrorReadLine();
                IntPtr window = IntPtr.Zero;
                Wait(delegate { window = Native.FindVideoWindow(child.Id); return window != IntPtr.Zero && current != null; }, "real D3D11 window and caps signal");
                var follower = new VideoWindowFollower();
                Resize(follower, window, 360, 780);
                Pass("initial portrait fits video ratio");

                Native.Rectangle outer;
                Native.GetWindowRect(window, out outer);
                Native.SetWindowPos(window, IntPtr.Zero, outer.Left, outer.Top, 620, 490, 0x4014);
                Wait(delegate { Native.GetWindowRect(window, out outer); return outer.Width == 620; }, "manual resize");
                Assert(!follower.Update(window, current), "same ratio preserves manual resize");
                Thread.Sleep(200);
                Native.GetWindowRect(window, out outer);
                Assert(outer.Width == 620 && outer.Height == 490, "manual dimensions preserved");

                child.StandardInput.WriteLine("780 360");
                Resize(follower, window, 780, 360);
                Pass("portrait to landscape");
                child.StandardInput.WriteLine("360 780");
                Resize(follower, window, 360, 780);
                Pass("landscape to portrait");

                ShowWindowAsync(window, 3);
                Wait(delegate { return Native.IsZoomed(window); }, "maximize");
                child.StandardInput.WriteLine("780 360");
                Wait(delegate { return current.Width == 780; }, "landscape while maximized");
                Assert(!follower.Update(window, current) && Native.IsZoomed(window), "maximized state preserved");
                ShowWindowAsync(window, 9);
                Wait(delegate { return !Native.IsZoomed(window); }, "restore from maximize");
                Resize(follower, window, 780, 360);
                Pass("pending rotation applied after maximize restore");

                ShowWindowAsync(window, 6);
                Wait(delegate { return Native.IsIconic(window); }, "minimize");
                child.StandardInput.WriteLine("360 780");
                Wait(delegate { return current.Width == 360; }, "portrait while minimized");
                Assert(!follower.Update(window, current) && Native.IsIconic(window), "minimized state preserved");
                ShowWindowAsync(window, 9);
                Wait(delegate { return !Native.IsIconic(window); }, "restore from minimize");
                Resize(follower, window, 360, 780);
                Pass("pending rotation applied after minimize restore");

                child.StandardInput.WriteLine("fullscreen true");
                Wait(delegate { return (Native.GetWindowStyle(window, -16) & 0x00C00000) == 0; }, "fullscreen");
                child.StandardInput.WriteLine("780 360");
                Wait(delegate { return current.Width == 780; }, "landscape while fullscreen");
                Assert(!follower.Update(window, current), "fullscreen preserved");
                child.StandardInput.WriteLine("fullscreen false");
                Wait(delegate { return (Native.GetWindowStyle(window, -16) & 0x00C00000) != 0; }, "leave fullscreen");
                Resize(follower, window, 780, 360);
                Pass("pending rotation applied after fullscreen exit");

                var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor);
                Native.SetWindowPos(window, IntPtr.Zero, monitor.Work.Right - 100, monitor.Work.Bottom - 100, 620, 490, 0x4014);
                Thread.Sleep(200);
                child.StandardInput.WriteLine("360 780");
                Resize(follower, window, 360, 780);
                Native.GetWindowRect(window, out outer);
                Assert(outer.Left >= monitor.Work.Left && outer.Right <= monitor.Work.Right &&
                    outer.Top >= monitor.Work.Top && outer.Bottom <= monitor.Work.Bottom, "rotated window kept inside monitor work area");
                Pass("window bounds clamped to current monitor");
            }
            catch (Exception ex) { report.AppendLine("FAIL: " + ex); result = 1; }
            finally
            {
                if (child != null)
                {
                    try { if (!child.HasExited) { child.StandardInput.WriteLine("quit"); if (!child.WaitForExit(3000)) child.Kill(); } }
                    finally { child.Dispose(); }
                }
                File.WriteAllText(Path.Combine(root, "artifacts", "rotation-test.txt"), report.ToString(), Encoding.UTF8);
                Console.Write(report);
            }
            return result;
        }

        private static string Caps(int width, int height)
        { return "DEBUG d3d11videosink set caps video/x-raw, width=(int)" + width + ", height=(int)" + height; }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Pass(string message) { report.AppendLine("PASS: " + message); }
        private static void Wait(Func<bool> predicate, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline) { if (predicate()) return; Thread.Sleep(50); }
            throw new Exception("Timed out: " + message);
        }
        private static void Resize(VideoWindowFollower follower, IntPtr window, int width, int height)
        {
            Wait(delegate { return current != null && current.Width == width && current.Height == height; }, "caps " + width + "x" + height);
            follower.Update(window, current);
            Wait(delegate
            {
                Native.Rectangle client;
                return Native.GetClientRect(window, out client) && client.Height > 0 &&
                    Math.Abs((double)client.Width / client.Height - current.Aspect) < 0.015;
            }, "client size follows " + width + "x" + height);
        }
        private static int Video(string root)
        {
            SetDllDirectory(Path.Combine(root, "runtime", "app", "_internal", "bin"));
            gst_init(IntPtr.Zero, IntPtr.Zero);
            IntPtr error;
            IntPtr pipeline = gst_parse_launch("videotestsrc is-live=true ! capsfilter name=shape caps=video/x-raw,width=360,height=780,framerate=30/1 ! videoconvert ! d3d11videosink name=sink fullscreen-toggle-mode=property", out error);
            if (pipeline == IntPtr.Zero || error != IntPtr.Zero) return 2;
            IntPtr shape = gst_bin_get_by_name(pipeline, "shape"), sink = gst_bin_get_by_name(pipeline, "sink");
            try
            {
                if (gst_element_set_state(pipeline, 4) == 0) return 3;
                string line;
                while ((line = Console.ReadLine()) != null && line != "quit")
                {
                    string[] parts = line.Split(' ');
                    if (parts[0] == "fullscreen") gst_util_set_object_arg(sink, "fullscreen", parts[1]);
                    else gst_util_set_object_arg(shape, "caps", "video/x-raw,width=" + parts[0] + ",height=" + parts[1] + ",framerate=30/1");
                }
            }
            finally
            {
                gst_element_set_state(pipeline, 1);
                gst_object_unref(shape); gst_object_unref(sink); gst_object_unref(pipeline);
            }
            return 0;
        }
    }
}
