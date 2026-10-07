using WorktreeSweep.Holders;

namespace WorktreeSweep.Tests;

/// <summary>
/// Finding the processes whose current folder lies in a folder, and turning a handle scanner's findings into holds; only children the
/// tests start, and this process, are looked for.
/// </summary>
public sealed class HolderFinderTests : IDisposable
{
    private const string Verbatim = @"\\?\";

    private readonly Fixture fx = new();

    /// <summary>Initializes a new instance of the <see cref="HolderFinderTests"/> class: a scanned folder with a subfolder, and a side
    /// folder outside it for ready files and bystanders.</summary>
    public HolderFinderTests()
    {
        Scanned = fx.PathTo("holders long folder name");
        Side = fx.PathTo("side");
        _ = Directory.CreateDirectory(Path.Join(Scanned, "sub folder"));
        _ = Directory.CreateDirectory(Side);
    }

    private string Scanned { get; }

    private string Side { get; }

    /// <summary>A child whose current folder is under the scanned folder is a holder, with its command line read.</summary>
    [Fact]
    public void CwdHolderIsFound()
    {
        using ReadyChild child = CwdChild();

        HolderReport report = HolderFinder.Find(Scanned, []);

        Assert.True(HoldsCurrentFolder(report, child.Id), $"pid {child.Id} not listed with its current folder: {Describe(report)}");
        Assert.Contains("-NoProfile", FindHolder(report, child.Id)?.CommandLine, StringComparison.Ordinal);
    }

    /// <summary>The folder given by its 8.3 short name still matches the child's long current folder.</summary>
    [Fact]
    public void ShortNameFolderMatches()
    {
        string shortName = NativeMethods.ShortPath(Scanned);
        Assert.SkipWhen(shortName.Equals(Scanned, StringComparison.OrdinalIgnoreCase), $"{Scanned} has no 8.3 name");
        using ReadyChild child = CwdChild();

        HolderReport report = HolderFinder.Find(shortName, []);

        Assert.True(HoldsCurrentFolder(report, child.Id), $"pid {child.Id} not found through {shortName}: {Describe(report)}");
    }

    /// <summary>A child whose current folder was set through a junction matches the folder given through that junction.</summary>
    [Fact]
    public void CwdThroughJunctionMatchesFolderAsGiven()
    {
        string link = Path.Join(Side, "link");
        Assert.SkipUnless(Fixture.MakeJunction(link, Scanned), "mklink /J is unavailable");
        using var child = ReadyChild.Start(Path.Join(link, "sub folder"), Ready("junction"));

        HolderReport report = HolderFinder.Find(link, []);

        Assert.True(HoldsCurrentFolder(report, child.Id), $"pid {child.Id} with its cwd under {link} not found: {Describe(report)}");
    }

    /// <summary>An excluded PID is listed neither as a holder nor as one that may hold the folder.</summary>
    [Fact]
    public void ExcludedPidIsNotListed()
    {
        using ReadyChild child = CwdChild();

        HolderReport report = HolderFinder.Find(Scanned, [child.Id]);

        Assert.Null(FindHolder(report, child.Id));
        Assert.DoesNotContain(report.MayHold, may => may.Pid == child.Id);
    }

    /// <summary>
    /// This process is never listed, and a child outside the folder is neither a holder nor a cannot-open entry. An unnamed-handle
    /// entry is not about the folder, so any process may show up with one.
    /// </summary>
    [Fact]
    public void OwnProcessIsNeverListed()
    {
        using var bystander = ReadyChild.Start(Side, Ready("bystander"));

        HolderReport report = HolderFinder.Find(Scanned, []);

        int own = Environment.ProcessId;
        Assert.Null(FindHolder(report, own));
        Assert.DoesNotContain(report.MayHold, may => may.Pid == own);
        Assert.Null(FindHolder(report, bystander.Id));
        Assert.DoesNotContain(report.MayHold, may => may.Pid == bystander.Id && may.Why == MayHoldWhy.CannotOpen);
    }

    /// <summary>A found holder is still the same process while it runs, and no longer once it has exited.</summary>
    [Fact]
    public void StillSameDetectsExit()
    {
        using ReadyChild child = CwdChild();
        HolderReport report = HolderFinder.Find(Scanned, []);
        Holder found = FindHolder(report, child.Id) ?? throw new InvalidOperationException($"pid {child.Id} not found: {Describe(report)}");

        Assert.True(HolderFinder.StillSame(found), $"live child reported as gone: {found}");
        child.Stop();
        Assert.False(HolderFinder.StillSame(found), $"exited child reported as live: {found}");
    }

    /// <summary>A 32-bit child's current folder is read from its 32-bit PEB; its command line is never read.</summary>
    [Fact]
    public void Wow64CwdHolderIsFoundWithoutCommandLine()
    {
        string powershell = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), @"WindowsPowerShell\v1.0\powershell.exe");
        Assert.SkipUnless(File.Exists(powershell), $"{powershell} is not installed");
        using var child = ReadyChild.Start(powershell, Path.Join(Scanned, "sub folder"), Ready("wow64"));

        HolderReport report = HolderFinder.Find(Scanned, []);

        Assert.True(HoldsCurrentFolder(report, child.Id), $"32-bit pid {child.Id} not listed with its current folder: {Describe(report)}");
        Assert.Null(FindHolder(report, child.Id)?.CommandLine);
    }

    /// <summary>
    /// A handle the scanner names under the folder makes the inspected child a holder of that file, its verbatim drive prefix dropped.
    /// </summary>
    [Fact]
    public void NamedHandleUnderTheFolderIsAnOpenHandleHold()
    {
        using var child = ReadyChild.Start(Side, Ready("named"));
        string file = HeldFile();
        int[] inspected = [];

        HolderReport report = HolderFinder.Find(
            Scanned,
            [],
            (pids, _) =>
            {
                inspected = [.. pids];
                return new HandleFindings([(child.Id, Verbatim + file)], [], []);
            }
        );

        Assert.Contains(child.Id, inspected);
        Assert.Equal<Hold>([new Hold.OpenHandle(file)], RequiredHolder(report, child.Id).Holds);
    }

    /// <summary>The same handle named twice is one hold.</summary>
    [Fact]
    public void TheSameHoldTwiceIsKeptOnce()
    {
        using var child = ReadyChild.Start(Side, Ready("twice"));
        string file = HeldFile();

        HolderReport report = HolderFinder.Find(Scanned, [], (_, _) => new HandleFindings([(child.Id, file), (child.Id, file)], [], []));

        Assert.Equal<Hold>([new Hold.OpenHandle(file)], RequiredHolder(report, child.Id).Holds);
    }

    /// <summary>A handle the scanner could not name makes its process one that may hold the folder.</summary>
    [Fact]
    public void UnnamedHandleIsAMayHold()
    {
        using var child = ReadyChild.Start(Side, Ready("unnamed"));

        HolderReport report = HolderFinder.Find(Scanned, [], (_, _) => new HandleFindings([(child.Id, null)], [], []));

        Assert.Null(FindHolder(report, child.Id));
        Assert.Contains(new MayHold(child.Id, "pwsh.exe", MayHoldWhy.UnnamedHandle), report.MayHold);
    }

    /// <summary>A process using the folder whose handles could not be listed may hold it, because it cannot be opened to see how.</summary>
    [Fact]
    public void UnlistedProcessUsingTheFolderIsACannotOpenMayHold()
    {
        using var child = ReadyChild.Start(Side, Ready("unlisted"));

        HolderReport report = HolderFinder.Find(Scanned, [], (_, _) => new HandleFindings([], [child.Id], [child.Id]));

        Assert.Contains(new MayHold(child.Id, "pwsh.exe", MayHoldWhy.CannotOpen), report.MayHold);
    }

    /// <summary>A holder that also uses the folder and could not be fully inspected is listed once, as a holder.</summary>
    [Fact]
    public void HolderUsingTheFolderIsNotAlsoAMayHold()
    {
        using ReadyChild child = CwdChild();

        HolderReport report = HolderFinder.Find(Scanned, [], (_, _) => new HandleFindings([], [child.Id], [child.Id]));

        Assert.True(HoldsCurrentFolder(report, child.Id), $"pid {child.Id} not listed with its current folder: {Describe(report)}");
        Assert.DoesNotContain(report.MayHold, may => may.Pid == child.Id);
    }

    /// <summary>
    /// An excluded process is in neither list, even when the scanner reports it holding a file, unlisted and using the folder. This
    /// process is excluded on purpose here.
    /// </summary>
    [Fact]
    public void ExcludedPidIsInNeitherListWhateverTheScannerReports()
    {
        int own = Environment.ProcessId;
        string file = HeldFile();

        HolderReport report = HolderFinder.Find(Scanned, [own], (_, _) => new HandleFindings([(own, file)], [own], [own]));

        Assert.Null(FindHolder(report, own));
        Assert.DoesNotContain(report.MayHold, may => may.Pid == own);
    }

    /// <inheritdoc/>
    public void Dispose() => fx.Dispose();

    private static Holder RequiredHolder(HolderReport report, int pid) =>
        FindHolder(report, pid) ?? throw new InvalidOperationException($"pid {pid} not listed as a holder: {Describe(report)}");

    private string HeldFile() => Path.Join(Scanned, "sub folder", "held.txt");

    private static Holder? FindHolder(HolderReport report, int pid) => report.Holders.FirstOrDefault(holder => holder.Pid == pid);

    private static bool HoldsCurrentFolder(HolderReport report, int pid) =>
        FindHolder(report, pid)?.Holds.Any(hold => hold is Hold.CurrentFolder) ?? false;

    private static string Describe(HolderReport report) =>
        $"holders [{string.Join("; ", report.Holders)}], may hold [{string.Join("; ", report.MayHold)}]";

    private ReadyChild CwdChild() => ReadyChild.Start(Path.Join(Scanned, "sub folder"), Ready("cwd"));

    private string Ready(string name) => Path.Join(Side, name);
}
