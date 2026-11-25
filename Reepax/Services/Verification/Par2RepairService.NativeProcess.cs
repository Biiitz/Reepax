using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    private sealed class NativeRepairProcess : IDisposable
    {
        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedJobLimits = 9;
        private const uint CreateSuspended = 0x4;
        private const uint ExtendedStartupInfo = 0x80000;
        private const uint CreateNoWindow = 0x8000000;
        private const uint UseStdHandles = 0x100;
        private const int HandleListAttribute = 0x20002;
        private const int JobListAttribute = 0x2000D;

        private readonly SafeFileHandle _job = CreateJobObjectW(0, null);
        private readonly AnonymousPipeServerStream _output = new(PipeDirection.In, HandleInheritability.Inheritable);
        private readonly AnonymousPipeServerStream _error = new(PipeDirection.In, HandleInheritability.Inheritable);
        private SafeFileHandle? _thread;
        private Process? _process;

        internal Process Process => _process!;
        internal Stream Output => _output;
        internal Stream Error => _error;

        internal static NativeRepairProcess Create(ProcessStartInfo info)
        {
            var native = new NativeRepairProcess();
            try
            {
                native.StartSuspended(info);
                return native;
            }
            catch
            {
                native.Dispose();
                throw;
            }
        }

        private void StartSuspended(ProcessStartInfo info)
        {
            var limits = new JobLimits { Basic = new BasicLimits { Flags = KillOnJobClose } };
            if (_job.IsInvalid || !SetInformationJobObject(_job, ExtendedJobLimits, ref limits, Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception();

            using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            var startup = new StartupInfoEx
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Flags = UseStdHandles,
                Input = input.ClientSafePipeHandle.DangerousGetHandle(),
                Output = _output.ClientSafePipeHandle.DangerousGetHandle(),
                Error = _error.ClientSafePipeHandle.DangerousGetHandle()
            };
            nint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            if (size <= 0) throw new Win32Exception();
            nint attributes = Marshal.AllocHGlobal(size);
            nint handles = Marshal.AllocHGlobal(IntPtr.Size * 4);
            bool initialized = false;
            try
            {
                if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception();
                initialized = true;
                Marshal.WriteIntPtr(handles, 0, startup.Input);
                Marshal.WriteIntPtr(handles, IntPtr.Size, startup.Output);
                Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, startup.Error);
                Marshal.WriteIntPtr(handles, 3 * IntPtr.Size, _job.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, handles, 3 * IntPtr.Size, 0, 0) ||
                    !UpdateProcThreadAttribute(attributes, 0, JobListAttribute, handles + 3 * IntPtr.Size, IntPtr.Size, 0, 0))
                    throw new Win32Exception();
                startup.Attributes = attributes;
                var command = new StringBuilder(string.Join(" ", info.ArgumentList.Prepend(info.FileName).Select(QuotePathArgument)));
                // The worker must belong to the job before the first native instruction can run.
                if (!CreateProcessW(info.FileName, command, 0, 0, true, CreateSuspended | ExtendedStartupInfo | CreateNoWindow,
                        0, info.WorkingDirectory, ref startup, out var child))
                    throw new Win32Exception();
                using var created = new SafeFileHandle(child.Process, ownsHandle: true);
                _thread = new SafeFileHandle(child.Thread, ownsHandle: true);
                _process = Process.GetProcessById(child.Id);
                _ = _process.SafeHandle;
                _output.DisposeLocalCopyOfClientHandle();
                _error.DisposeLocalCopyOfClientHandle();
                input.DisposeLocalCopyOfClientHandle();
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(handles);
                Marshal.FreeHGlobal(attributes);
            }
        }

        private static string QuotePathArgument(string value)
        {
            if (value.Contains('"')) throw new ArgumentException("Invalid PAR2 argument.");
            return "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
        }

        internal void Resume()
        {
            using var thread = _thread!;
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
        }

        internal void Stop() => TerminateJobObject(_job, 1);

        public void Dispose()
        {
            _job.Dispose();
            _thread?.Dispose();
            _process?.Dispose();
            _output.Dispose();
            _error.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public nuint MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public int Size;
            public nint Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            public ushort ShowWindow, ReservedLength;
            public nint Reserved2, Input, Output, Error, Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public nint Process, Thread; public int Id, ThreadId; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits limits, int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(nint attributes, int count, int flags, ref nint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(nint attributes, int flags, nint attribute, nint value, nint size,
            nint previous, nint returned);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(nint attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string executable, StringBuilder command, nint processAttributes,
            nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment,
            string directory, ref StartupInfoEx startup, out ProcessInfo process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(SafeFileHandle thread);
    }
}
