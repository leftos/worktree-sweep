using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WorktreeSweep.Holders;
using WorktreeSweep.Processes;

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

    /// <summary>
    /// A child holding a file under a Greek folder, shared with no one, is named by the scan with the Greek path intact and the
    /// handle's value: names are read in UTF-16, where <c>handle.exe</c> printed <c>?</c> for every character outside the code page.
    /// </summary>
    [Fact]
    public void ScanNamesAGreekPathIntact()
    {
        string folder = fx.PathTo("δοκιμή");
        _ = Directory.CreateDirectory(folder);
        string file = Path.Join(folder, "αρχείο.txt");
        File.WriteAllText(file, "held");
        string ready = fx.PathTo("ready-greek");
        using var child = ReadyChild.Run(fx.Root, ready, HoldScript(file, ready));
        IReadOnlyList<string> roots = HolderFinder.FolderForms(folder);

        HandleFindings findings = OpenHandles.Scan([child.Id], roots);

        NamedHandle held = Assert.Single(findings.Names, handle => handle.Name is string name && LockedPaths.Matches(name, roots));
        Assert.Equal(child.Id, held.Pid);
        Assert.EndsWith(@"\δοκιμή\αρχείο.txt", held.Name, StringComparison.Ordinal);
        Assert.NotEqual(nint.Zero, held.Handle);
    }

    /// <summary>The scan asks the file system for the users of every root it is given, not only the first.</summary>
    [Fact]
    public void ScanListsTheUsersOfEveryRoot()
    {
        string first = fx.PathTo("first");
        string second = fx.PathTo("second");
        _ = Directory.CreateDirectory(first);
        _ = Directory.CreateDirectory(second);
        using var firstChild = ReadyChild.Start(first, fx.PathTo("ready-first"));
        using var secondChild = ReadyChild.Start(second, fx.PathTo("ready-second"));

        HandleFindings findings = OpenHandles.Scan([], [.. HolderFinder.FolderForms(first), .. HolderFinder.FolderForms(second)]);

        Assert.Contains(firstChild.Id, findings.Using);
        Assert.Contains(secondChild.Id, findings.Using);
    }

    /// <inheritdoc/>
    public void Dispose() => fx.Dispose();

    /// <summary>A child script that holds <paramref name="file"/> shared with no one, then writes <paramref name="ready"/>.</summary>
    private static string HoldScript(string file, string ready) =>
        $"$f = [IO.File]::Open({ReadyChild.Quoted(file)}, 'Open', 'Read', 'None'); "
        + $"Set-Content -LiteralPath {ReadyChild.Quoted(ready)} ready; Start-Sleep 300";

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
