using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Wdk.Storage.FileSystem;
using Windows.Wdk.System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Threading;
using WorktreeSweep.Discovery;
using WdkPInvoke = Windows.Wdk.PInvoke;

namespace WorktreeSweep.Holders;

/// <summary>One handle of another process, still to be checked.</summary>
/// <param name="Pid">The process.</param>
/// <param name="Source">The process, open with <see cref="OpenHandles.OpenSource"/>, shared by every handle from it.</param>
/// <param name="Value">The handle's value in that process.</param>
internal sealed record RemoteHandle(int Pid, SafeHandle Source, ulong Value);

/// <summary>
/// Lists other processes' open file handles, names the ones on disk, and asks the file system which processes use a folder. The
/// handle list comes from <c>NtQueryInformationProcess(ProcessHandleInformation)</c>; its <c>PROCESS_HANDLE_SNAPSHOT_INFORMATION</c>
/// is missing from the CsWin32 metadata, so it is read by the offsets below.
/// </summary>
internal static class OpenHandles
{
    /// <summary>Header of a <c>PROCESS_HANDLE_SNAPSHOT_INFORMATION</c>: the handle count and a reserved word.</summary>
    private const int HandleListHeader = 16;

    /// <summary>One <c>PROCESS_HANDLE_TABLE_ENTRY_INFO</c>; the handle value is its first 8 bytes.</summary>
    private const int HandleEntrySize = 40;

    /// <summary><c>PROCESS_HANDLE_TABLE_ENTRY_INFO.ObjectTypeIndex</c>.</summary>
    private const int HandleEntryType = 28;

    /// <summary>The first handle list buffer size, in bytes.</summary>
    private const int FirstHandleListSize = 64 * 1024;

    /// <summary>The first <c>FILE_PROCESS_IDS_USING_FILE_INFORMATION</c> buffer size, in bytes.</summary>
    private const int FirstPidListSize = 4096;

    /// <summary><c>FILE_PROCESS_IDS_USING_FILE_INFORMATION.ProcessIdList</c>: after the count and its padding.</summary>
    private const int PidListStart = 8;

    /// <summary>How many buffer sizes a growing query tries.</summary>
    private const int QueryAttempts = 8;

    /// <summary>
    /// Lists and names the open disk handles of <paramref name="pids"/>, and finds the processes using the folder; this is the
    /// production <see cref="HandleScanner"/>. A process that cannot be opened or listed is unlisted; a failure to find the
    /// processes using the folder is traced and leaves that list empty.
    /// </summary>
    /// <param name="pids">The inspected processes.</param>
    /// <param name="roots">Every spelling of the folder, the fully resolved one first.</param>
    /// <returns>What the handles showed.</returns>
    /// <exception cref="IOException">This program's own file or handles cannot be read to learn the File type index.</exception>
    internal static HandleFindings Scan(IReadOnlyCollection<int> pids, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(roots);
        uint fileType = FileTypeIndex();
        var sources = new List<SafeFileHandle>();
        try
        {
            var handles = new List<(int Pid, RemoteHandle Handle)>();
            var unlisted = new List<int>();
            foreach (int pid in pids)
            {
                SafeFileHandle? source = OpenSource(pid);
                IReadOnlyList<ulong>? values = source is null ? null : FileHandleValues(source, fileType);
                if (source is null || values is null)
                {
                    Trace.WriteLine($"cannot list the handles of pid {pid}");
                    source?.Dispose();
                    unlisted.Add(pid);
                    continue;
                }
                sources.Add(source);
                handles.AddRange(values.Select(value => (pid, new RemoteHandle(pid, source, value))));
            }
            NamedHandles named = HandleNaming.Name(
                handles,
                Check,
                HandleNaming.DefaultWorkers,
                HandleNaming.DefaultTimeout,
                HandleNaming.CancelLookup
            );
            return Findings(named, unlisted, UsingOrNone(roots[0]));
        }
        finally
        {
            sources.ForEach(source => source.Dispose());
        }
    }

    /// <summary>
    /// The findings of a scan: the names, plus an unnamed handle for each process whose check failed at once and that the file system
    /// reports as using the folder. A failed check on any other process adds nothing: volume handles and the like fail at once, so a
    /// failure alone does not show that the process holds the folder.
    /// </summary>
    /// <param name="named">What naming the handles found.</param>
    /// <param name="unlisted">Processes whose handles could not be listed.</param>
    /// <param name="usingPids">Processes the file system reports as using the folder.</param>
    /// <returns>The findings.</returns>
    internal static HandleFindings Findings(NamedHandles named, IReadOnlyCollection<int> unlisted, IReadOnlyList<int> usingPids)
    {
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(usingPids);
        IEnumerable<(int Pid, string? Name)> failedUsing = usingPids.Where(named.Failed.Contains).Distinct().Select(pid => (pid, (string?)null));
        return new HandleFindings([.. named.Names, .. failedUsing], unlisted, usingPids);
    }

    /// <summary>
    /// The object type index of File handles, learnt from this program's own file, which this process opens and then finds among its
    /// own handles. The index differs between Windows builds, so it is looked up on every scan.
    /// </summary>
    /// <returns>The index.</returns>
    /// <exception cref="IOException">This program's file cannot be opened, or this process's handles cannot be listed or lack it.</exception>
    internal static uint FileTypeIndex()
    {
        string exe = Environment.ProcessPath ?? throw new IOException("cannot find this program's path to learn the File type index");
        using SafeFileHandle file = File.OpenHandle(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using SafeFileHandle own = PInvoke.GetCurrentProcess_SafeHandle();
        IReadOnlyList<(ulong Value, uint Type)> entries =
            HandleEntries(own) ?? throw new IOException("cannot list this process's own handles to learn the File type index");
        ulong value = (ulong)file.DangerousGetHandle();
        foreach ((ulong Value, uint Type) entry in entries)
        {
            if (entry.Value == value)
            {
                return entry.Type;
            }
        }
        throw new IOException($"cannot find the File object type among this process's handles: {exe} is not among them");
    }

    /// <summary>Opens a process to list its handles and duplicate them.</summary>
    /// <param name="pid">The process.</param>
    /// <returns>The open process, or <see langword="null"/> when it cannot be opened with that access.</returns>
    internal static SafeFileHandle? OpenSource(int pid) =>
        ProcessQuery.Open(pid, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE);

    /// <summary>Each open handle of a process: its value and its object type index.</summary>
    /// <param name="process">A process open with query access.</param>
    /// <returns>The handles, or <see langword="null"/> when they cannot be listed.</returns>
    internal static IReadOnlyList<(ulong Value, uint Type)>? HandleEntries(SafeHandle process)
    {
        byte[]? buffer = HandleSnapshot(process);
        if (buffer is null)
        {
            return null;
        }
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        if (count > (ulong)((buffer.Length - HandleListHeader) / HandleEntrySize))
        {
            return null;
        }
        var entries = new List<(ulong Value, uint Type)>((int)count);
        for (int index = 0; index < (int)count; index++)
        {
            Span<byte> entry = buffer.AsSpan(HandleListHeader + (index * HandleEntrySize), HandleEntrySize);
            entries.Add((BinaryPrimitives.ReadUInt64LittleEndian(entry), BinaryPrimitives.ReadUInt32LittleEndian(entry[HandleEntryType..])));
        }
        return entries;
    }

    /// <summary>
    /// The values of a process's File handles. Nothing is duplicated here: a duplicate keeps the other process's file open and would
    /// make its delete or rename fail, so each is duplicated only while it is checked.
    /// </summary>
    /// <param name="process">A process open with query access.</param>
    /// <param name="fileType">The File type index, from <see cref="FileTypeIndex"/>.</param>
    /// <returns>The values, or <see langword="null"/> when the handles cannot be listed.</returns>
    internal static IReadOnlyList<ulong>? FileHandleValues(SafeHandle process, uint fileType) =>
        HandleEntries(process)?.Where(entry => entry.Type == fileType).Select(entry => entry.Value).ToList();

    /// <summary>
    /// Duplicates one handle, names it when it is a file on disk, and closes the duplicate before returning, even when the lookup was
    /// cancelled. Reading the type can hang as well as reading the name (a synchronous file waits for its owner's pending I/O), so
    /// this runs on a <see cref="HandleNaming"/> worker and both count as the lookup.
    /// </summary>
    /// <param name="handle">The handle and the process holding it.</param>
    /// <param name="lookupStarting">Called once the handle is duplicated, before its type and name are read.</param>
    /// <param name="lookupEnded">Called once its type and name are read, before the duplicate is closed.</param>
    /// <returns>The name; Skipped when the handle is not a file on disk; Failed when it cannot be duplicated or named.</returns>
    internal static CheckedHandle Check(RemoteHandle handle, Action lookupStarting, Action lookupEnded)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(lookupStarting);
        ArgumentNullException.ThrowIfNull(lookupEnded);
        using var remote = new SafeFileHandle((nint)handle.Value, ownsHandle: false);
        using SafeFileHandle own = PInvoke.GetCurrentProcess_SafeHandle();
        if (
            !PInvoke.DuplicateHandle(
                handle.Source,
                remote,
                own,
                out SafeFileHandle duplicate,
                0,
                false,
                DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS
            )
        )
        {
            int error = Marshal.GetLastPInvokeError();
            duplicate.Dispose();
            Trace.WriteLine($"cannot duplicate handle 0x{handle.Value:X} of pid {handle.Pid}: {new Win32Exception(error).Message}");
            return CheckedHandle.Failed;
        }
        try
        {
            lookupStarting();
            if (PInvoke.GetFileType(duplicate) != FILE_TYPE.FILE_TYPE_DISK)
            {
                return CheckedHandle.Skipped;
            }
            string? name = PathResolver.FinalPathOf(duplicate, out int lookupError);
            if (name is null)
            {
                // Volume handles and the like fail here at once, so a failure is not a sign of a hung lookup and does not by itself
                // make the process unnamed.
                Trace.WriteLine($"cannot name handle 0x{handle.Value:X} of pid {handle.Pid}: {new Win32Exception(lookupError).Message}");
                return CheckedHandle.Failed;
            }
            return CheckedHandle.Disk(name);
        }
        finally
        {
            lookupEnded();
            duplicate.Dispose();
        }
    }

    /// <summary>The processes the file system reports as using <paramref name="path"/> itself.</summary>
    /// <param name="path">A file or folder.</param>
    /// <returns>Their PIDs.</returns>
    /// <exception cref="IOException">The path cannot be opened, or the list cannot be read.</exception>
    internal static IReadOnlyList<int> PidsUsing(string path)
    {
        using SafeFileHandle file = PInvoke.CreateFile(
            path,
            (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            null
        );
        if (file.IsInvalid)
        {
            throw new IOException($"cannot open {path}", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        using var raw = BorrowedHandle.Of(file);
        int size = FirstPidListSize;
        for (int attempt = 0; attempt < QueryAttempts; attempt++)
        {
            byte[] buffer = new byte[size];
            NTSTATUS status = WdkPInvoke.NtQueryInformationFile(raw.Value, out _, buffer, FILE_INFORMATION_CLASS.FileProcessIdsUsingFileInformation);
            if (status == NTSTATUS.STATUS_INFO_LENGTH_MISMATCH || status == NTSTATUS.STATUS_BUFFER_OVERFLOW)
            {
                size *= 4;
                continue;
            }
            if (status.Value < 0)
            {
                throw new IOException($"cannot list the processes using {path}: NTSTATUS 0x{status.Value:X8}");
            }
            return ParsePids(buffer, path);
        }
        throw new IOException($"the list of processes using {path} kept growing");
    }

    /// <summary>The processes using <paramref name="root"/>, or none when they cannot be listed, which is traced.</summary>
    private static IReadOnlyList<int> UsingOrNone(string root)
    {
        try
        {
            return PidsUsing(root);
        }
        catch (IOException error)
        {
            Trace.TraceWarning($"cannot list the processes using {root}: {error.Message}");
            return [];
        }
    }

    /// <summary>The PIDs in a <c>FILE_PROCESS_IDS_USING_FILE_INFORMATION</c>: a 32-bit count, then 64-bit PIDs from offset 8.</summary>
    private static List<int> ParsePids(byte[] buffer, string path)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (count > (uint)((buffer.Length - PidListStart) / sizeof(ulong)))
        {
            throw new IOException($"malformed list of processes using {path}: {count} entries do not fit");
        }
        var pids = new List<int>((int)count);
        for (int index = 0; index < (int)count; index++)
        {
            ulong pid = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(PidListStart + (index * sizeof(ulong))));
            pids.Add(pid <= int.MaxValue ? (int)pid : throw new IOException($"malformed list of processes using {path}: PID {pid}"));
        }
        return pids;
    }

    /// <summary>
    /// The process's <c>PROCESS_HANDLE_SNAPSHOT_INFORMATION</c>, in a buffer grown until it fits, or <see langword="null"/> when it
    /// cannot be read.
    /// </summary>
    private static unsafe byte[]? HandleSnapshot(SafeHandle process)
    {
        using var raw = BorrowedHandle.Of(process);
        long size = FirstHandleListSize;
        for (int attempt = 0; attempt < QueryAttempts; attempt++)
        {
            byte[] buffer = new byte[size];
            uint needed = 0;
            NTSTATUS status;
            fixed (byte* data = buffer)
            {
                status = WdkPInvoke.NtQueryInformationProcess(
                    raw.Value,
                    PROCESSINFOCLASS.ProcessHandleInformation,
                    data,
                    (uint)buffer.Length,
                    ref needed
                );
            }
            if (
                status == NTSTATUS.STATUS_INFO_LENGTH_MISMATCH
                || status == NTSTATUS.STATUS_BUFFER_TOO_SMALL
                || status == NTSTATUS.STATUS_BUFFER_OVERFLOW
            )
            {
                size = Math.Max(needed, size * 2);
                if (size > Array.MaxLength)
                {
                    return null;
                }
                continue;
            }
            return status.Value >= 0 ? buffer : null;
        }
        return null;
    }
}
