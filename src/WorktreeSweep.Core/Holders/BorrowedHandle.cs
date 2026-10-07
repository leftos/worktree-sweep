using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace WorktreeSweep.Holders;

/// <summary>
/// The raw value of a <see cref="SafeHandle"/>, for a native call that takes a <see cref="HANDLE"/>. The handle cannot be closed
/// until this is disposed, so the raw value stays valid for the calls made with it.
/// </summary>
internal readonly ref struct BorrowedHandle
{
    private readonly SafeHandle handle;

    private BorrowedHandle(SafeHandle handle)
    {
        this.handle = handle;
        Value = new HANDLE(handle.DangerousGetHandle());
    }

    /// <summary>Gets the raw value.</summary>
    internal HANDLE Value { get; }

    /// <summary>Borrows the raw value of <paramref name="handle"/>.</summary>
    /// <param name="handle">An open handle.</param>
    /// <returns>The borrowed value, to dispose once the native calls are made.</returns>
    /// <exception cref="ObjectDisposedException">The handle is already closed.</exception>
    internal static BorrowedHandle Of(SafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        bool added = false;
        handle.DangerousAddRef(ref added);
        return new BorrowedHandle(handle);
    }

    /// <summary>Lets the handle be closed again.</summary>
    public void Dispose() => handle.DangerousRelease();
}
