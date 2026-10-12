using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace IPhoneMirror
{
    internal sealed class FirewallSession : IDisposable
    {
        private NamedPipeServerStream pipe;
        private StreamReader reader;
        private StreamWriter writer;
        private Process guard;
        internal bool CleanupConfirmed { get; private set; }
        internal bool Healthy { get { return guard != null && !guard.HasExited && pipe != null && pipe.IsConnected; } }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);

        internal void Open(string root)
        {
            string script = Path.Combine(root, "scripts", "FirewallGuard.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("방화벽 관리 스크립트가 없습니다.", script);
            string name = "IPhoneMirror-" + Guid.NewGuid().ToString("N");
            var access = new PipeSecurity();
            access.SetAccessRuleProtection(true, false);
            access.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, access);
            try
            {
                var connected = pipe.BeginWaitForConnection(null, null);
                using (var owner = Process.GetCurrentProcess())
                {
                    string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                    var info = new ProcessStartInfo(powershell)
                    {
                        UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -OwnerId " + owner.Id +
                            " -OwnerStarted " + owner.StartTime.ToUniversalTime().Ticks + " -PipeName " + name,
                        WorkingDirectory = root
                    };
                    try { guard = Process.Start(info); }
                    catch (Win32Exception ex)
                    {
                        if (ex.NativeErrorCode == 1223) throw new InvalidOperationException("관리자 승인이 취소되어 미러링을 시작하지 않았습니다.", ex);
                        throw;
                    }
                }
                // UAC runs before this deadline. Closing the UI does not leave rules:
                // the guard checks the owner's retained process handle throughout.
                if (!connected.AsyncWaitHandle.WaitOne(20000)) throw new TimeoutException("방화벽 관리 연결 시간이 초과되었습니다.");
                pipe.EndWaitForConnection(connected);
                connected.AsyncWaitHandle.Close();
                uint client;
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out client) || guard == null || client != guard.Id)
                    throw new InvalidOperationException("방화벽 관리 프로세스가 일치하지 않습니다.");
                reader = new StreamReader(pipe, new UTF8Encoding(false));
                writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                Expect("HELLO");
                writer.WriteLine("OPEN");
                Expect("READY");
            }
            catch
            {
                // Disconnect tells the independent guard to roll back. Never kill
                // the guard while it is responsible for cleaning its own rules.
                ReleaseConnection();
                throw;
            }
        }

        internal void Bind(Process receiver)
        {
            writer.WriteLine("BIND " + receiver.Id + " " + receiver.StartTime.ToUniversalTime().Ticks);
            Expect("BOUND");
        }

        private void Expect(string expected)
        {
            Task<string> response = reader.ReadLineAsync();
            if (!response.Wait(30000)) throw new TimeoutException("방화벽 처리 시간이 초과되었습니다. 진단 로그를 확인해 주세요.");
            string line = response.GetAwaiter().GetResult();
            if (line != null && line.StartsWith("ERROR ", StringComparison.Ordinal))
                throw new InvalidOperationException("방화벽 처리 실패: " + Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(6))));
            if (line != expected) throw new IOException("방화벽 관리 연결이 종료되었습니다. 다시 시작하여 정리 상태를 확인해 주세요.");
        }

        public void Dispose()
        {
            try
            {
                if (writer != null)
                {
                    try { writer.WriteLine("STOP"); }
                    catch (IOException) { /* The guard may have already observed receiver exit. Read its final reply. */ }
                    Expect("CLOSED");
                    CleanupConfirmed = true;
                }
            }
            finally { ReleaseConnection(); }
        }

        private void ReleaseConnection()
        {
            // Do not flush a broken StreamWriter during exception cleanup.
            if (pipe != null) { pipe.Dispose(); pipe = null; }
            reader = null; writer = null;
            if (guard != null) { guard.Dispose(); guard = null; }
        }
    }

    internal sealed class ReceiverJob : IDisposable
    {
        private IntPtr handle;
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal UIntPtr MinWorkingSet, MaxWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        internal ReceiverJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error);
            }
        }
        internal void Add(Process receiver)
        { if (!AssignProcessToJobObject(handle, receiver.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void Dispose()
        { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
