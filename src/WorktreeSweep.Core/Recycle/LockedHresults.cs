using Windows.Win32.Foundation;

namespace WorktreeSweep.Recycle;

/// <summary>The HRESULTs a Shell recycle fails with when something holds the tree.</summary>
/// <remarks>
/// A Win32 sharing violation (32) or access denied (5), or the copy engine's sharing violation or access denied on the source.
/// Despite its name, the copy engine reports <c>COPYENGINE_E_SHARING_VIOLATION_DEST</c> for a file held open, or a working folder,
/// inside the tree being recycled. <c>COPYENGINE_E_ACCESS_DENIED_SRC</c> is what a file held with delete sharing gives. A folder
/// whose ACL denies delete looks the same; the unlock flow then finds nothing holding it, and the retry fails with the same error.
/// </remarks>
public static class LockedHresults
{
    /// <summary>The severity bit and <c>FACILITY_WIN32</c> that <c>HRESULT_FROM_WIN32</c> (<c>winerror.h</c>) puts above a Win32 code.</summary>
    private const int Win32Facility = unchecked((int)0x80070000);

    private static readonly int[] Locked =
    [
        FromWin32(WIN32_ERROR.ERROR_SHARING_VIOLATION),
        FromWin32(WIN32_ERROR.ERROR_ACCESS_DENIED),
        HRESULT.COPYENGINE_E_SHARING_VIOLATION_SRC,
        HRESULT.COPYENGINE_E_SHARING_VIOLATION_DEST,
        HRESULT.COPYENGINE_E_ACCESS_DENIED_SRC,
    ];

    /// <summary>
    /// Whether <paramref name="call"/> failing with <paramref name="hr"/> says something holds the tree. Only
    /// <see cref="ShellCall.PerformOperations"/> touches the tree: an access denied from any other call, such as
    /// <see cref="ShellCall.SHCreateItemFromParsingName"/>, is a plain failure with no holder to find.
    /// </summary>
    /// <param name="call">The Shell call that failed.</param>
    /// <param name="hr">The HRESULT it returned.</param>
    /// <returns><see langword="true"/> for one of the five lock HRESULTs from <see cref="ShellCall.PerformOperations"/>.</returns>
    public static bool IsLocked(ShellCall call, int hr) => call == ShellCall.PerformOperations && Locked.Contains(hr);

    /// <summary><c>HRESULT_FROM_WIN32</c>, which CsWin32 does not generate: a macro, not an export.</summary>
    private static int FromWin32(WIN32_ERROR error) => Win32Facility | (int)error;
}

/// <summary>The Shell calls one recycle makes, in the order it makes them.</summary>
public enum ShellCall
{
    /// <summary><c>CoCreateInstance</c> of the Shell's <c>FileOperation</c>.</summary>
    CoCreateInstance,

    /// <summary><c>IFileOperation::SetOperationFlags</c>.</summary>
    SetOperationFlags,

    /// <summary><c>IFileOperation::SetOwnerWindow</c>.</summary>
    SetOwnerWindow,

    /// <summary><c>SHCreateItemFromParsingName</c>, which names the folder as a Shell item.</summary>
    SHCreateItemFromParsingName,

    /// <summary><c>IFileOperation::DeleteItem</c>, which only queues the delete.</summary>
    DeleteItem,

    /// <summary><c>IFileOperation::PerformOperations</c>, which recycles the tree.</summary>
    PerformOperations,

    /// <summary><c>IFileOperation::GetAnyOperationsAborted</c>.</summary>
    GetAnyOperationsAborted,
}
