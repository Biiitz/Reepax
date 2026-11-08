using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Reepax.Services.Storage;

namespace Reepax.Services.SystemIntegration;

internal sealed class WindowsKeepAwakeRequest : SafeHandleZeroOrMinusOneIsInvalid
{
    private bool _systemRequired;
    private bool _awayModeRequired;

    private WindowsKeepAwakeRequest() : base(ownsHandle: true) { }

    internal static WindowsKeepAwakeRequest Create()
    {
        var context = new ReasonContext { Version = 0, Flags = 1 };
        context.Reason.SimpleReasonString = Marshal.StringToHGlobalUni("Reepax is downloading or extracting files.");
        WindowsKeepAwakeRequest request;
        int createError;
        try
        {
            request = PowerCreateRequest(ref context);
            createError = Marshal.GetLastWin32Error();
        }
        finally { Marshal.FreeHGlobal(context.Reason.SimpleReasonString); }
        try
        {
            if (request.IsInvalid) throw new Win32Exception(createError);
            if (!PowerSetRequest(request, RequestType.SystemRequired))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            request._systemRequired = true;
            request._awayModeRequired = PowerSetRequest(request, RequestType.AwayModeRequired);
            if (!request._awayModeRequired)
                AppLogger.Warn($"[PowerManagement] Away mode is unavailable; system sleep prevention remains active. Windows error code: {Marshal.GetLastWin32Error()}.");
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    protected override bool ReleaseHandle()
    {
        // Closing the request also releases its requirements if an explicit clear fails.
        if (_awayModeRequired) PowerClearRequest(handle, RequestType.AwayModeRequired);
        if (_systemRequired) PowerClearRequest(handle, RequestType.SystemRequired);
        return CloseHandle(handle);
    }

    private enum RequestType { SystemRequired = 1, AwayModeRequired = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public ReasonUnion Reason;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonUnion
    {
        [FieldOffset(0)] public IntPtr SimpleReasonString;
        [FieldOffset(0)] public DetailedReason Detailed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedReason
    {
        public IntPtr Module;
        public uint ReasonId;
        public uint StringCount;
        public IntPtr Strings;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern WindowsKeepAwakeRequest PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(WindowsKeepAwakeRequest request, RequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, RequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
