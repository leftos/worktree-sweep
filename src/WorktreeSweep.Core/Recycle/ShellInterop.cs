// IFileOperation and IShellItem are declared here by hand as [GeneratedComInterface] interfaces. CsWin32 could generate them, but
// under this project's "allowMarshaling": false it emits pointer-based COM structs called through raw vtables, which are hard to
// read; these declarations read as ordinary C# interfaces and are marshalled by the .NET COM source generator.
//
// A COM interface is a vtable: methods are found by position, not by name. Every method of IFileOperation up to the last one used
// (GetAnyOperationsAborted) is declared in the order of shobjidl_core.h (Windows SDK 10.0.26100.0); the unused ones take raw
// pointers, as they are never called. IShellItem is only passed back to the Shell, so it declares none of its methods.
// SetOperationFlags takes a uint rather than CsWin32's FILEOPERATION_FLAGS: one source generator cannot see another's output.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WorktreeSweep.Recycle;

/// <summary>The Shell's <c>IShellItem</c>: a file or folder as the Shell names it.</summary>
[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem;

/// <summary>The Shell's <c>IFileOperation</c>: copies, moves and deletes queued up and then performed together.</summary>
[GeneratedComInterface]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    [PreserveSig]
    int Advise(nint sink, out uint cookie);

    [PreserveSig]
    int Unadvise(uint cookie);

    [PreserveSig]
    int SetOperationFlags(uint flags);

    [PreserveSig]
    int SetProgressMessage(nint message);

    [PreserveSig]
    int SetProgressDialog(nint dialog);

    [PreserveSig]
    int SetProperties(nint properties);

    [PreserveSig]
    int SetOwnerWindow(nint owner);

    [PreserveSig]
    int ApplyPropertiesToItem(nint item);

    [PreserveSig]
    int ApplyPropertiesToItems(nint items);

    [PreserveSig]
    int RenameItem(nint item, nint newName, nint sink);

    [PreserveSig]
    int RenameItems(nint items, nint newName);

    [PreserveSig]
    int MoveItem(nint item, nint destinationFolder, nint newName, nint sink);

    [PreserveSig]
    int MoveItems(nint items, nint destinationFolder);

    [PreserveSig]
    int CopyItem(nint item, nint destinationFolder, nint copyName, nint sink);

    [PreserveSig]
    int CopyItems(nint items, nint destinationFolder);

    /// <summary>Queues <paramref name="item"/> for deletion, or recycling under <c>FOFX_RECYCLEONDELETE</c>.</summary>
    [PreserveSig]
    int DeleteItem(IShellItem item, nint sink);

    [PreserveSig]
    int DeleteItems(nint items);

    [PreserveSig]
    int NewItem(nint destinationFolder, uint fileAttributes, nint name, nint templateName, nint sink);

    /// <summary>Performs the queued operations.</summary>
    [PreserveSig]
    int PerformOperations();

    /// <summary>Whether any operation was cancelled by the user or stopped by a failure.</summary>
    [PreserveSig]
    int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool anyAborted);
}
