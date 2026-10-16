using System.Security.Principal;
using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The production locker scan over children the tests start: the elevated scan of every process, and the re-check before a
/// close.</summary>
public sealed class LockerScanTests : IDisposable
{
    /// <summary>The command that runs the elevated test, named when the run is not elevated.</summary>
    private const string RunCommand =
        "sudo dotnet test WorktreeSweep.slnx -c Release --no-build -- --filter-method *ElevatedScanFindsTheHolderOfAGreekPath --explicit on";

    private readonly Fixture fx = new();

    /// <summary>
    /// Elevated, the scan over every process finds a child holding <c>δοκιμή\αρχείο.txt</c> under the fixture folder, shared with no
    /// one, with the Greek path intact. Run by hand, elevated, from the repo root after a Release build:
    /// <c>sudo dotnet test WorktreeSweep.slnx -c Release --no-build -- --filter-method *ElevatedScanFindsTheHolderOfAGreekPath --explicit on</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public void ElevatedScanFindsTheHolderOfAGreekPath()
    {
        if (!Elevated())
        {
            Assert.Skip($"run elevated: {RunCommand}");
        }
        string folder = fx.PathTo("δοκιμή");
        _ = Directory.CreateDirectory(folder);
        string file = Path.Join(folder, "αρχείο.txt");
        File.WriteAllText(file, "held");
        string ready = fx.PathTo("ready-elevated");
        using var child = ReadyChild.Run(fx.Root, ready, HoldScript(file, ready));
        int own = Environment.ProcessId;
        IReadOnlyList<int> pids = [.. ProcessTable.Snapshot().Keys.Where(pid => !ProcessTable.SystemPids.Contains(pid) && pid != own)];

        LockerFindings found = LockerScan.Elevated().Find([folder], pids);

        Locker locker = Assert.Single(found.Lockers, candidate => candidate.Pid == child.Id);
        HeldHandle held = Assert.Single(locker.Handles);
        Assert.EndsWith(@"\δοκιμή\αρχείο.txt", held.Name, StringComparison.Ordinal);
        child.Stop();
    }

    /// <summary>
    /// The re-check confirms a handle a same-user child still holds under the folder, and refuses it under another start time, an
    /// unknown start time, or a folder the handle is not under.
    /// </summary>
    [Fact]
    public void StillHoldsConfirmsOnlyTheSameProcessAndFolder()
    {
        string folder = fx.PathTo("held");
        string other = fx.PathTo("other");
        _ = Directory.CreateDirectory(folder);
        _ = Directory.CreateDirectory(other);
        string file = Path.Join(folder, "held.txt");
        File.WriteAllText(file, "held");
        string ready = fx.PathTo("ready-held");
        using var child = ReadyChild.Run(fx.Root, ready, HoldScript(file, ready));
        var scan = new LockerScan();
        Locker locker = Assert.Single(scan.Find([folder], [child.Id]).Lockers);
        HeldHandle held = Assert.Single(locker.Handles);

        Assert.True(scan.StillHolds(child.Id, locker.Started, held.Handle, [folder]));
        Assert.False(scan.StillHolds(child.Id, locker.Started + 1, held.Handle, [folder]));
        Assert.False(scan.StillHolds(child.Id, null, held.Handle, [folder]));
        Assert.False(scan.StillHolds(child.Id, locker.Started, held.Handle, [other]));
    }

    /// <inheritdoc/>
    public void Dispose() => fx.Dispose();

    /// <summary>Whether this process's token is in the Administrator role.</summary>
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>A child script that holds <paramref name="file"/> shared with no one, then writes <paramref name="ready"/>.</summary>
    private static string HoldScript(string file, string ready) =>
        $"$f = [IO.File]::Open({ReadyChild.Quoted(file)}, 'Open', 'Read', 'None'); "
        + $"Set-Content -LiteralPath {ReadyChild.Quoted(ready)} ready; Start-Sleep 300";
}
