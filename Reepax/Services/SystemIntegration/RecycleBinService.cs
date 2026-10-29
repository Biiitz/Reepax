using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Reepax.Services.SystemIntegration;

internal static class RecycleBinService
{
    internal static void Move(IReadOnlyCollection<string> paths)
    {
        var fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (fullPaths.Length == 0)
            return;
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            MoveOnSta(fullPaths);
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { MoveOnSta(fullPaths); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void MoveOnSta(string[] paths)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!;
        var items = new List<IShellItem>();
        try
        {
            operation.SetOperationFlags(OperationFlags.Silent | OperationFlags.NoConfirmation |
                OperationFlags.NoErrorUI | OperationFlags.NoConnectedElements | OperationFlags.WantNukeWarning |
                OperationFlags.RecycleOnDelete | OperationFlags.EarlyFailure | OperationFlags.AddUndoRecord);
            var sink = new RecycleOnlySink();
            var shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            foreach (var path in paths)
            {
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                SHCreateItemFromParsingName(path, 0, ref shellItemId, out var item);
                items.Add(item);
                operation.DeleteItem(item, sink);
            }
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted || sink.Failure < 0 || paths.Any(path => File.Exists(path) || Directory.Exists(path)))
                throw new IOException("The selected paths could not all be moved to the Recycle Bin.");
            GC.KeepAlive(sink);
        }
        finally
        {
            Marshal.ReleaseComObject(operation);
            foreach (var item in items)
                Marshal.ReleaseComObject(item);
        }
    }

    [Flags]
    private enum OperationFlags : uint
    {
        Silent = 0x0004,
        NoConfirmation = 0x0010,
        NoErrorUI = 0x0400,
        NoConnectedElements = 0x2000,
        WantNukeWarning = 0x4000,
        RecycleOnDelete = 0x00080000,
        EarlyFailure = 0x00100000,
        AddUndoRecord = 0x20000000
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint bindContext, ref Guid interfaceId,
        out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    // Unused methods preserve the native COM vtable order.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(OperationFlags flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(nint item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(nint item, nint name, nint sink);
        void RenameItems(nint items, nint name);
        void MoveItem(nint item, nint destination, nint name, nint sink);
        void MoveItems(nint items, nint destination);
        void CopyItem(nint item, nint destination, nint name, nint sink);
        void CopyItems(nint items, nint destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems(nint items);
        void NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint name);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint name, int result, nint created);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int result, nint created);
        [PreserveSig] int PreNewItem(uint flags, nint destination, nint name);
        [PreserveSig] int PostNewItem(uint flags, nint destination, nint name, nint template, uint attributes, int result, nint created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class RecycleOnlySink : IFileOperationProgressSink
    {
        internal int Failure { get; private set; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) { if (result < 0) Failure = result; return 0; }
        public int PreRenameItem(uint flags, nint item, nint name) => 0;
        public int PostRenameItem(uint flags, nint item, nint name, int result, nint created) => 0;
        public int PreMoveItem(uint flags, nint item, nint destination, nint name) => 0;
        public int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created) => 0;
        public int PreCopyItem(uint flags, nint item, nint destination, nint name) => 0;
        public int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created) => 0;
        public int PreDeleteItem(uint flags, nint item)
        {
            const uint recycleIfPossible = 0x80;
            if ((flags & recycleIfPossible) == 0)
                return Failure = unchecked((int)0x80004004);
            return 0;
        }
        public int PostDeleteItem(uint flags, nint item, int result, nint created)
        {
            if (result < 0) Failure = result;
            return 0;
        }
        public int PreNewItem(uint flags, nint destination, nint name) => 0;
        public int PostNewItem(uint flags, nint destination, nint name, nint template, uint attributes, int result, nint created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
