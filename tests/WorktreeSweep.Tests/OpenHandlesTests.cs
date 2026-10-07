using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WorktreeSweep.Holders;

namespace WorktreeSweep.Tests;

/// <summary>Listing, checking and naming open handles, on this process's own handles and on children the tests start.</summary>
public sealed class OpenHandlesTests : IDisposable
{
    private readonly Fixture fx = new();

    /// <summary>The File type index is the object type of a file this process opens.</summary>
    [Fact]
    public void FileTypeIndexIsTheTypeOfAnOpenedFile()
    {
        using SafeFileHandle file = OpenNewFile("second.txt");
        using SafeFileHandle own = OwnProcess();

        uint fileType = OpenHandles.FileTypeIndex();

        IReadOnlyList<(ulong Value, uint Type)> entries = OpenHandles.HandleEntries(own) ?? throw new InvalidOperationException("no handle list");
        Assert.Contains((ValueOf(file), fileType), entries);
    }

    /// <summary>This process's File handles include a file it has just opened.</summary>
    [Fact]
    public void OwnFileHandlesIncludeAJustOpenedFile()
    {
        using SafeFileHandle file = OpenNewFile("opened.txt");
        using SafeFileHandle own = OwnProcess();

        IReadOnlyList<ulong>? values = OpenHandles.FileHandleValues(own, OpenHandles.FileTypeIndex());

        Assert.NotNull(values);
        Assert.Contains(ValueOf(file), values);
    }

    /// <summary>A handle that is not a file on disk, here a pipe, is skipped.</summary>
    [Fact]
    public void PipeHandleIsSkipped()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using SafeFileHandle own = OwnProcess();

        CheckedHandle checkedPipe = OpenHandles.Check(
            new RemoteHandle(Environment.ProcessId, own, ValueOf(pipe.SafePipeHandle)),
            () => { },
            () => { }
        );

        Assert.Equal(CheckedHandle.Skipped, checkedPipe);
    }

    /// <summary>
    /// A volume handle is a disk handle whose name lookup fails at once; its check fails rather than timing out, so the process
    /// holding it is not made an unnamed-handle holder of every folder, only of one the file system reports it using.
    /// </summary>
    [Fact]
    public void VolumeHandleCheckFails()
    {
        string volume = @"\\.\" + Path.GetPathRoot(AppContext.BaseDirectory)?.TrimEnd('\\');
        using SafeFileHandle handle = NativeMethods.OpenVolume(volume);
        using SafeFileHandle own = OwnProcess();

        CheckedHandle checkedVolume = OpenHandles.Check(new RemoteHandle(Environment.ProcessId, own, ValueOf(handle)), () => { }, () => { });

        Assert.Equal(CheckedHandle.Failed, checkedVolume);
    }

    /// <summary>A child whose current folder is the folder is among the processes the file system reports using it.</summary>
    [Fact]
    public void PidsUsingListsAChildWhoseCurrentFolderIsTheFolder()
    {
        string folder = fx.PathTo("used");
        _ = Directory.CreateDirectory(folder);
        using var child = ReadyChild.Start(folder, fx.PathTo("ready"));

        IReadOnlyList<int> pids = OpenHandles.PidsUsing(folder);

        Assert.Contains(child.Id, pids);
    }

    /// <summary>A folder that cannot be opened gives no processes using it, rather than failing the scan.</summary>
    [Fact]
    public void MissingFolderGivesNoUsingThroughTheScanner()
    {
        HandleFindings findings = OpenHandles.Scan([], [fx.PathTo("missing")]);

        Assert.Empty(findings.Using);
    }

    /// <summary>A process that cannot be opened, here the idle process, is unlisted and names nothing.</summary>
    [Fact]
    public void UnopenablePidIsUnlisted()
    {
        HandleFindings findings = OpenHandles.Scan([0], [fx.Root]);

        Assert.Equal([0], findings.Unlisted);
        Assert.Empty(findings.Names);
    }

    /// <inheritdoc/>
    public void Dispose() => fx.Dispose();

    private static ulong ValueOf(SafeHandle handle) => (ulong)handle.DangerousGetHandle();

    private static SafeFileHandle OwnProcess() =>
        OpenHandles.OpenSource(Environment.ProcessId) ?? throw new InvalidOperationException("cannot open this process to list its handles");

    private SafeFileHandle OpenNewFile(string name)
    {
        string path = fx.PathTo(name);
        File.WriteAllText(path, name);
        return File.OpenHandle(path);
    }
}
