using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IPhoneMirror
{
    internal sealed class Receiver : IDisposable
    {
        internal const int Port = 7100;
        internal readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        private Process process;
        private readonly object logLock = new object();
        internal string LastError = "";
        internal string Code { get; private set; }
        internal string LogPath { get { return System.IO.Path.Combine(Root, "logs", "receiver.log"); } }
        internal string EnginePath { get { return System.IO.Path.Combine(Root, "runtime", "app", "_internal", "bin", "uxplay.exe"); } }
        internal int ProcessId { get { return process == null ? 0 : process.Id; } }
        internal bool Running { get { return process != null && !process.HasExited; } }
        internal bool Ready { get; private set; }
        internal volatile VideoGeometry Video;

        private void Log(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return;
            lock (logLock)
            {
                try { File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("o") + " " + line + Environment.NewLine, Encoding.UTF8); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal void Start(bool audio, bool software)
        {
            if (Running) return;
            Dispose();
            Ready = false;
            LastError = "";
            Video = null;
            if (!File.Exists(EnginePath)) throw new FileNotFoundException("미러링 엔진이 없습니다. setup-runtime.ps1을 먼저 실행해 주세요.", EnginePath);
            // Check TCP and UDP separately: another receiver must never be mistaken for ours.
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            if (ip.GetActiveTcpListeners().Any(p => p.Port >= Port && p.Port <= Port + 2) ||
                ip.GetActiveUdpListeners().Any(p => p.Port >= Port && p.Port <= Port + 2))
                throw new InvalidOperationException("미러링용 포트(7100–7102)를 다른 프로그램이 사용 중입니다. 다른 미러링 앱을 종료한 뒤 다시 시작해 주세요.");

            Directory.CreateDirectory(System.IO.Path.Combine(Root, "logs"));
            Directory.CreateDirectory(System.IO.Path.Combine(Root, "state"));
            var config = System.IO.Path.Combine(Root, "state", "uxplayrc");
            File.WriteAllText(config, "# Settings are supplied by iPhone Mirror.\n", Encoding.ASCII);
            byte[] bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Code = (100000 + BitConverter.ToUInt32(bytes, 0) % 900000).ToString();
            var runtime = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(EnginePath));
            var info = new ProcessStartInfo(EnginePath);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.WorkingDirectory = Root;
            info.Arguments = "-rc \"" + config + "\" -n \"iPhone Mirror\" -nh -p 7100 -s 1920x1080 -fps 60 -vs d3d11videosink -vsync no -nofreeze -pw " + Code;
            if (!audio) info.Arguments += " -as 0";
            if (software) info.Arguments += " -avdec";
            info.EnvironmentVariables["GST_PLUGIN_PATH"] = System.IO.Path.Combine(runtime, "lib", "gstreamer-1.0");
            info.EnvironmentVariables["GST_PLUGIN_SYSTEM_PATH"] = info.EnvironmentVariables["GST_PLUGIN_PATH"];
            info.EnvironmentVariables["GST_REGISTRY"] = System.IO.Path.Combine(Root, "state", "gstreamer-registry.bin");
            info.EnvironmentVariables["PATH"] = System.IO.Path.GetDirectoryName(EnginePath) + ";" + Environment.GetEnvironmentVariable("PATH");
            // No external plugin scanner is bundled; inspect plugins inside the receiver process.
            info.EnvironmentVariables["GST_REGISTRY_FORK"] = "no";
            // The pinned sink reports negotiated dimensions when caps change.
            // Limit diagnostics to this category; no packet/frame tracing is needed.
            info.EnvironmentVariables["GST_DEBUG"] = "d3d11videosink:5";
            info.EnvironmentVariables["GST_DEBUG_NO_COLOR"] = "1";
            info.EnvironmentVariables.Remove("GST_DEBUG_FILE");
            Log("Starting receiver; audio=" + audio + "; software=" + software + ". Connection code omitted.");
            process = new Process { StartInfo = info };
            process.OutputDataReceived += ReadLine;
            process.ErrorDataReceived += ReadLine;
            try
            {
                if (!process.Start()) throw new InvalidOperationException("수신기를 실행하지 못했습니다.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch { Dispose(); throw; }
        }

        private void ReadLine(object sender, DataReceivedEventArgs e)
        {
            if (!Object.ReferenceEquals(sender, process)) return;
            var line = e.Data;
            if (String.IsNullOrWhiteSpace(line)) return;
            var video = VideoGeometry.Parse(line);
            if (video != null)
            {
                Video = video;
                Log("Video dimensions: " + video.Width + "x" + video.Height);
            }
            // Retain warnings/errors, but avoid writing verbose sink diagnostics.
            if (line.Contains("d3d11videosink") && line.Contains("DEBUG")) return;
            // Connection codes are shown only in the UI, not saved in diagnostic logs.
            if (line.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("pin code", StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (!String.IsNullOrEmpty(Code)) line = line.Replace(Code, "[code]");
            if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) LastError = line;
            Log(line);
        }

        internal bool Probe()
        {
            if (!Running) return false;
            try
            {
                using (var client = new TcpClient())
                {
                    // UxPlay uses base+0 for mirror data and base+1 for RTSP control.
                    var task = client.ConnectAsync("127.0.0.1", Port + 1);
                    if (!task.Wait(250)) return false;
                    task.GetAwaiter().GetResult();
                    client.ReceiveTimeout = 600;
                    client.SendTimeout = 600;
                    var stream = client.GetStream();
                    var request = Encoding.ASCII.GetBytes("OPTIONS * RTSP/1.0\r\nCSeq: 1\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    var response = new byte[2048];
                    var count = stream.Read(response, 0, response.Length);
                    var header = Encoding.ASCII.GetString(response, 0, count);
                    Ready = header.StartsWith("RTSP/1.0 200") || header.StartsWith("HTTP/1.1 200");
                    if (!Ready) LastError = "Local probe: " + header.Split('\r')[0];
                    if (Ready) Log("Local RTSP OPTIONS check succeeded. iPhone discovery and video still require device verification.");
                    return Ready;
                }
            }
            catch (SocketException) { return false; }
            catch (IOException) { return false; }
            catch (AggregateException) { return false; }
        }

        internal void Stop()
        {
            Ready = false;
            Video = null;
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    // This CLI has no IPC stop command. Terminate only the process we started.
                    process.Kill();
                    process.WaitForExit(3000);
                }
                Log("Receiver stopped; exit=" + process.ExitCode);
            }
            catch (InvalidOperationException) { /* Process creation may have failed before it acquired a PID. */ }
            finally { process.Dispose(); process = null; Code = null; }
        }
        public void Dispose() { Stop(); }
    }

    internal sealed class Controller : IDisposable
    {
        internal readonly Window Window;
        private readonly Receiver receiver = new Receiver();
        private readonly VideoWindowFollower videoWindow = new VideoWindowFollower();
        private readonly Button start;
        private readonly CheckBox audio, software;
        private readonly TextBlock status, code, detail;
        private readonly System.Windows.Shapes.Ellipse dot;
        private readonly DispatcherTimer timer;
        private DateTime started;

        internal Controller()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            start = (Button)Window.FindName("StartButton");
            audio = (CheckBox)Window.FindName("AudioCheck");
            software = (CheckBox)Window.FindName("SoftwareCheck");
            status = (TextBlock)Window.FindName("StatusText");
            code = (TextBlock)Window.FindName("CodeText");
            detail = (TextBlock)Window.FindName("DetailText");
            dot = (System.Windows.Shapes.Ellipse)Window.FindName("StatusDot");
            start.Click += delegate { Toggle(); };
            ((Button)Window.FindName("HelpButton")).Click += delegate
            {
                MessageBox.Show(Window,
                    "• PC와 아이폰을 같은 공유기에 연결해 주세요. PC는 유선 연결도 가능합니다.\n\n" +
                    "• 목록에 PC가 없으면 Windows 네트워크 프로필, 방화벽, VPN 또는 공유기의 기기 격리를 확인해 주세요.\n\n" +
                    "• Windows 방화벽 알림이 나타나면 사용 중인 신뢰하는 네트워크에서 uxplay.exe 통신을 허용해 주세요.\n\n" +
                    "• 화면이 검거나 멈추면 중지 후 ‘호환 모드’를 켜고 다시 연결해 주세요.\n\n" +
                    "• 첫 버전은 화면 보기 전용입니다. 터치·홈·키보드는 다음 개발 단계입니다. iOS 27은 실제 아이폰으로 호환성을 확인해야 합니다.",
                    "연결 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            ((Button)Window.FindName("LogButton")).Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(receiver.LogPath));
                    if (!File.Exists(receiver.LogPath)) File.WriteAllText(receiver.LogPath, "아직 수신기를 실행하지 않았습니다.", Encoding.UTF8);
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + receiver.LogPath + "\"") { UseShellExecute = true });
                }
                catch (Exception ex) { MessageBox.Show(Window, ex.Message, "로그를 열지 못했습니다"); }
            };
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += delegate { UpdateStatus(); };
            Window.Closed += delegate { Dispose(); };
        }

        private void SetStatus(string text, string color, string description)
        {
            status.Text = text;
            dot.Fill = (Brush)new BrushConverter().ConvertFromString(color);
            detail.Text = description;
        }

        internal void Toggle()
        {
            try
            {
                if (receiver.Running)
                {
                    timer.Stop();
                    receiver.Stop();
                    ResetControls();
                    SetStatus("미러링을 중지했습니다", "#9AA2B4", "다시 시작하면 새로운 연결 코드가 표시됩니다.");
                    return;
                }
                receiver.Start(audio.IsChecked == true, software.IsChecked == true);
                videoWindow.Reset();
                started = DateTime.UtcNow;
                code.Text = receiver.Code;
                start.Content = "미러링 중지";
                audio.IsEnabled = software.IsEnabled = false;
                SetStatus("수신기를 시작하고 있습니다", "#D49A42", "준비가 완료되면 아이폰에서 화면 미러링을 선택해 주세요.");
                timer.Start();
            }
            catch (Exception ex)
            {
                timer.Stop();
                receiver.Stop();
                ResetControls();
                SetStatus("시작하지 못했습니다", "#D75C64", ex.Message);
            }
        }

        private void ResetControls()
        {
            start.Content = "미러링 시작";
            audio.IsEnabled = software.IsEnabled = true;
            code.Text = "— — — — — —";
        }

        private void UpdateStatus()
        {
            try
            {
                if (!receiver.Running)
                {
                    timer.Stop();
                    ResetControls();
                    SetStatus("수신기가 종료되었습니다", "#D75C64", "진단 로그에서 원인을 확인해 주세요. " + receiver.LastError);
                    return;
                }
                var handle = Native.FindVideoWindow(receiver.ProcessId);
                videoWindow.Update(handle, receiver.Video);
                if (handle != IntPtr.Zero)
                {
                    SetStatus("아이폰 화면 창이 열렸습니다", "#35A47D", "아이폰 회전에 맞춰 영상 창 크기도 바뀝니다. Alt + Enter로 전체 화면을 전환합니다.");
                }
                else if (receiver.Ready || receiver.Probe())
                {
                    SetStatus("아이폰 연결을 기다리고 있습니다", "#35A47D", "제어 센터 → 화면 미러링 → iPhone Mirror. 목록이 보이지 않으면 연결 도움말을 확인해 주세요.");
                }
                else if ((DateTime.UtcNow - started).TotalSeconds > 15)
                {
                    SetStatus("수신기 상태를 확인해 주세요", "#D49A42", "준비 상태를 확인하지 못했습니다. 진단 로그와 Bonjour Service 실행 여부를 확인해 주세요.");
                }
            }
            catch (Exception ex)
            {
                timer.Stop();
                SetStatus("상태를 확인하지 못했습니다", "#D75C64", ex.Message);
            }
        }

        public void Dispose()
        {
            timer.Stop();
            receiver.Dispose();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                if (args.Contains("--smoke")) return Smoke(root);
                var app = new Application();
                if (args.Contains("--render"))
                {
                    using (var controller = new Controller())
                    {
                        var surface = (FrameworkElement)controller.Window.Content;
                        surface.Measure(new Size(550, 760));
                        surface.Arrange(new Rect(0, 0, 550, 760));
                        surface.UpdateLayout();
                        var image = new RenderTargetBitmap(550, 760, 96, 96, PixelFormats.Pbgra32);
                        image.Render(surface);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        Directory.CreateDirectory(System.IO.Path.Combine(root, "artifacts"));
                        using (var output = File.Create(System.IO.Path.Combine(root, "artifacts", "preview.png"))) encoder.Save(output);
                    }
                    return 0;
                }
                bool created;
                using (var mutex = new Mutex(true, "Local\\IPhoneMirrorPrototype", out created))
                {
                    if (!created) { MessageBox.Show("iPhone Mirror가 이미 실행 중입니다.", "iPhone Mirror"); return 0; }
                    using (var controller = new Controller())
                    {
                        if (args.Contains("--start")) controller.Window.Loaded += delegate { controller.Toggle(); };
                        app.Run(controller.Window);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(System.IO.Path.Combine(root, "logs"));
                File.WriteAllText(System.IO.Path.Combine(root, "logs", "application-error.log"), ex.ToString());
                if (!args.Contains("--smoke") && !args.Contains("--render")) MessageBox.Show(ex.Message, "iPhone Mirror");
                return 1;
            }
        }

        private static int Smoke(string root)
        {
            var report = new StringBuilder();
            int result = 0;
            Directory.CreateDirectory(System.IO.Path.Combine(root, "artifacts"));
            try
            {
                using (var receiver = new Receiver())
                {
                    for (int cycle = 1; cycle <= 2; cycle++)
                    {
                        receiver.Start(false, cycle == 2);
                        var deadline = DateTime.UtcNow.AddSeconds(20);
                        while (DateTime.UtcNow < deadline && receiver.Running && !receiver.Probe()) Thread.Sleep(400);
                        if (!receiver.Running || !receiver.Ready) throw new Exception("RTSP receiver did not become ready. " + receiver.LastError);
                        report.AppendLine("PASS: receiver start and RTSP OPTIONS response, cycle " + cycle);
                        int pid = receiver.ProcessId;
                        receiver.Stop();
                        bool alive = false;
                        try { using (var process = Process.GetProcessById(pid)) alive = !process.HasExited; }
                        catch (ArgumentException) { }
                        if (alive) throw new Exception("Receiver process survived Stop.");
                        report.AppendLine("PASS: owned receiver process terminated, cycle " + cycle);
                        Thread.Sleep(500);
                    }
                }
                report.AppendLine("NOT TESTED: iPhone 17 Pro Max / iOS 27 discovery, pairing, video, rotation, audio and reconnect.");
            }
            catch (Exception ex) { report.AppendLine("FAIL: " + ex); result = 1; }
            File.WriteAllText(System.IO.Path.Combine(root, "artifacts", "smoke-test.txt"), report.ToString(), Encoding.UTF8);
            return result;
        }
    }
}
