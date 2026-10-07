using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using WorktreeSweep.Removal;

namespace WorktreeSweep.Recycle;

/// <summary>Moves a folder to the Recycle Bin through the Shell's <c>IFileOperation</c>.</summary>
public static class ShellRecycler
{
    /// <summary>
    /// Recycle with undo, warn before any permanent delete, show no progress or error UI, and stop at the first failure, which leaves
    /// the tree whole.
    /// </summary>
    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE
        | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING
        | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR
        | FILEOPERATION_FLAGS.FOF_SILENT
        | FILEOPERATION_FLAGS.FOF_NOERRORUI
        | FILEOPERATION_FLAGS.FOFX_EARLYFAILURE;

    /// <summary>
    /// Creates the wrappers that <see cref="Wrap{T}"/> makes and <see cref="Release"/> releases. The runtime's own instance, which the
    /// generated marshallers use, is not public.
    /// </summary>
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    /// <summary>
    /// Moves <paramref name="path"/> to the Recycle Bin. The Shell work runs on a new single-threaded-apartment thread, which this call
    /// waits for. The only prompt the Shell can still show is its permanent-delete warning: the flags never allow a silent permanent
    /// delete when the Shell decides the item cannot be recycled. A lock is retried as <see cref="RecycleRetry"/> says, and only the
    /// last attempt decides <see cref="LockedException"/>.
    /// </summary>
    /// <param name="path">The folder to recycle.</param>
    /// <exception cref="LockedException">
    /// A file or folder in the tree is in use: <c>PerformOperations</c> failed with one of the <see cref="LockedHresults"/>. Nothing was
    /// recycled. Its inner exception is the Shell failure, naming the call and its HRESULT.
    /// </exception>
    /// <exception cref="IOException">
    /// A Shell call failed, the user answered No to the permanent-delete warning or cancelled, or the folder is still there afterwards.
    /// </exception>
    public static void Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        bool aborted = OnStaThread(() => RecycleRetry.WhileLocked(() => DeleteOnce(path), Thread.Sleep));
        if (aborted)
        {
            throw new IOException($"moving {path} to the Recycle Bin was cancelled or aborted");
        }
        if (Path.Exists(path))
        {
            throw new IOException($"{path} is still there after moving it to the Recycle Bin");
        }
    }

    /// <summary>Runs <paramref name="work"/> on a new STA thread and waits for it; its exception is rethrown here.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        })
        {
            IsBackground = true,
            Name = "worktree-sweep recycle",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>Runs one Shell delete with undo on the calling STA thread; returns whether any operation was aborted.</summary>
    private static bool DeleteOnce(string path)
    {
        IFileOperation operation = CreateFileOperation(path);
        try
        {
            Check(operation.SetOperationFlags((uint)Flags), ShellCall.SetOperationFlags, path);
            IShellItem item = ParseShellItem(path);
            try
            {
                Check(operation.DeleteItem(item, sink: 0), ShellCall.DeleteItem, path);
            }
            finally
            {
                Release(item);
            }
            Check(operation.PerformOperations(), ShellCall.PerformOperations, path);
            Check(operation.GetAnyOperationsAborted(out bool anyAborted), ShellCall.GetAnyOperationsAborted, path);
            return anyAborted;
        }
        finally
        {
            Release(operation);
        }
    }

    private static unsafe IFileOperation CreateFileOperation(string path)
    {
        Guid clsid = typeof(FileOperation).GUID;
        Guid iid = typeof(IFileOperation).GUID;
        void* instance;
        HRESULT hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_ALL, &iid, &instance);
        Check(hr, ShellCall.CoCreateInstance, path);
        return Wrap<IFileOperation>(instance);
    }

    private static unsafe IShellItem ParseShellItem(string path)
    {
        Guid iid = typeof(IShellItem).GUID;
        void* item;
        HRESULT hr;
        fixed (char* chars = path)
        {
            hr = PInvoke.SHCreateItemFromParsingName(new PCWSTR(chars), null, &iid, &item);
        }
        Check(hr, ShellCall.SHCreateItemFromParsingName, path);
        return Wrap<IShellItem>(item);
    }

    /// <summary>
    /// Wraps a COM pointer the caller owns in a unique managed object, which takes its own reference; the caller's is released. When the
    /// object does not implement <typeparamref name="T"/>, the new wrapper is released here before the cast's exception goes on.
    /// </summary>
    private static unsafe T Wrap<T>(void* pointer)
    {
        object wrapper;
        try
        {
            wrapper = ComWrappers.GetOrCreateObjectForComInstance((nint)pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release((nint)pointer);
        }
        try
        {
            return (T)wrapper;
        }
        catch (InvalidCastException)
        {
            Release(wrapper);
            throw;
        }
    }

    /// <summary>Releases a wrapper's COM reference now, on this STA thread, rather than from the finalizer thread.</summary>
    private static void Release(object wrapper) => ((ComObject)wrapper).FinalRelease();

    private static void Check(int hr, ShellCall call, string path)
    {
        if (hr < 0)
        {
            throw Failure(call, hr, path);
        }
    }

    /// <summary>
    /// The exception for <paramref name="call"/> failing with <paramref name="hr"/>: a <see cref="LockedException"/> when
    /// <see cref="LockedHresults.IsLocked"/> says so, holding the Shell failure as its inner exception; otherwise the Shell failure.
    /// </summary>
    /// <param name="call">The Shell call that failed.</param>
    /// <param name="hr">The HRESULT it returned.</param>
    /// <param name="path">The folder being recycled.</param>
    /// <returns>The exception to throw.</returns>
    internal static IOException Failure(ShellCall call, int hr, string path)
    {
        string message = string.Create(CultureInfo.InvariantCulture, $"cannot move {path} to the Recycle Bin: {call} failed with 0x{hr:X8}");
        var failure = new IOException(message, hr);
        return LockedHresults.IsLocked(call, hr) ? new LockedException(path, firstLockedFile: null, failure) : failure;
    }
}
