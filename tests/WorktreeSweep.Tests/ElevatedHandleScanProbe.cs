using System.Diagnostics;
using System.Security.Principal;
using WorktreeSweep.Holders;
using WorktreeSweep.Processes;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>
/// Measures the C# handle scan (<see cref="OpenHandles.Scan"/>) over every process while elevated, beside <c>handle.exe</c>: how long
/// it takes, how many processes it opens, which of them it sees under a probe folder, and what <see cref="OpenHandles.PidsUsing"/> adds
/// for a held file and for a memory-mapped one. It writes nothing to the shipped tool; it exists to be run by hand.
/// </summary>
public sealed class ElevatedHandleScanProbe
{
    /// <summary>The command that runs this probe, named when the run is not elevated.</summary>
    private const string RunCommand =
        "sudo dotnet test WorktreeSweep.slnx -c Release --no-build -- --filter-method *MeasureTheHandleScanElevated --explicit on";

    /// <summary>The file the output block is also written to, under <see cref="Path.GetTempPath"/>.</summary>
    private const string ResultsFile = "worktree-sweep-probe-wts41.txt";

    /// <summary>
    /// Measures <see cref="OpenHandles.Scan"/> over every process while this process is elevated, beside <c>handle.exe</c>, and
    /// asserts only that the positive control — a child holding a file share-none — is found. Its output goes to the test's
    /// diagnostic messages and to <c>%TEMP%\worktree-sweep-probe-wts41.txt</c>. Run by hand, elevated, from the repo root after a
    /// Release build:
    /// <c>sudo dotnet test WorktreeSweep.slnx -c Release --no-build -- --filter-method *MeasureTheHandleScanElevated --explicit on</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public void MeasureTheHandleScanElevated()
    {
        if (!Elevated())
        {
            Assert.Skip($"run elevated: {RunCommand}");
        }

        Process.EnterDebugMode();

        using var fx = new Fixture();
        string probe = fx.PathTo("probe");
        _ = Directory.CreateDirectory(probe);
        string held = Path.Join(probe, "held.txt");
        string mapped = Path.Join(probe, "mapped.bin");
        File.WriteAllText(held, "held");
        File.WriteAllBytes(mapped, new byte[4096]);

        using var side = new Fixture();
        string positiveReady = side.PathTo("ready-positive");
        string sectionReady = side.PathTo("ready-section");
        using var positive = ReadyChild.Run(side.Root, positiveReady, PositiveScript(held, positiveReady));
        using var section = ReadyChild.Run(side.Root, sectionReady, SectionScript(mapped, sectionReady));

        var labels = new Dictionary<int, string> { [positive.Id] = "positive control", [section.Id] = "section only" };
        IReadOnlyList<int> pids = ScannedPids();
        IReadOnlyList<string> roots = HolderFinder.FolderForms(probe);
        var clock = Stopwatch.StartNew();
        HandleFindings findings = OpenHandles.Scan(pids, roots);
        clock.Stop();
        IReadOnlyList<int> underFolder = UnderFolder(findings, roots);

        var lines = new List<string>
        {
            "worktree-sweep handle scan probe (WTS-41)",
            "elevated: yes",
            "debug mode: on",
            $"positive control pid: {positive.Id}",
            $"section only pid: {section.Id}",
        };
        lines.AddRange(ScanLines(pids, findings, underFolder, clock.ElapsedMilliseconds, labels));
        lines.AddRange(UsingLines(probe, held, mapped));
        lines.AddRange(HandleExeLines(probe));
        string block = string.Join(Environment.NewLine, lines);

        TestContext.Current.SendDiagnosticMessage(block);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), ResultsFile), block);

        // A local, not the Assert argument: xUnit's analyzers would otherwise replace the block with their own message.
        bool positiveFound = underFolder.Contains(positive.Id);
        Assert.True(positiveFound, block);
    }

    /// <summary>Whether this process's token is in the Administrator role.</summary>
    /// <returns><see langword="true"/> when elevated.</returns>
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Every running process this probe scans: the process table without 0, 4 and this process.</summary>
    /// <returns>The pids.</returns>
    private static IReadOnlyList<int> ScannedPids()
    {
        IReadOnlyDictionary<int, ProcessEntry> table = ProcessTable.Snapshot();
        return [.. table.Keys.Where(pid => pid != 0 && pid != 4 && pid != Environment.ProcessId).Order()];
    }

    /// <summary>The pids whose named handle lies under one of <paramref name="roots"/>.</summary>
    /// <param name="findings">What the scan found.</param>
    /// <param name="roots">Every spelling of the folder.</param>
    /// <returns>The pids, sorted.</returns>
    private static IReadOnlyList<int> UnderFolder(HandleFindings findings, IReadOnlyList<string> roots)
    {
        var found = new HashSet<int>();
        foreach ((int Pid, string? Name) handle in findings.Names)
        {
            if (handle.Name is not null && LockedPaths.Matches(handle.Name, roots))
            {
                _ = found.Add(handle.Pid);
            }
        }
        return [.. found.Order()];
    }

    /// <summary>The scan's own counts and the pids it placed under the folder.</summary>
    /// <param name="pids">The pids the scan was given.</param>
    /// <param name="findings">What the scan found.</param>
    /// <param name="underFolder">The pids named under the folder.</param>
    /// <param name="millis">How long the scan took.</param>
    /// <param name="labels">The probe's children, by pid.</param>
    /// <returns>The lines.</returns>
    private static List<string> ScanLines(
        IReadOnlyList<int> pids,
        HandleFindings findings,
        IReadOnlyList<int> underFolder,
        long millis,
        IReadOnlyDictionary<int, string> labels
    )
    {
        var lines = new List<string>
        {
            $"processes scanned: {pids.Count}",
            $"scan: {millis} ms",
            $"opened: {pids.Count - findings.Unlisted.Count}, not opened: {findings.Unlisted.Count}",
            $"scan pids under the folder: {LabeledPids(underFolder, labels)}",
            $"file system pids using the folder: {PidList(findings.Using)}",
        };
        return lines;
    }

    /// <summary>One timed <see cref="OpenHandles.PidsUsing"/> line per path.</summary>
    /// <param name="probe">The probe folder.</param>
    /// <param name="held">The share-none held file.</param>
    /// <param name="mapped">The memory-mapped file.</param>
    /// <returns>The lines.</returns>
    private static IReadOnlyList<string> UsingLines(string probe, string held, string mapped) =>
        [UsingLine(probe), UsingLine(held), UsingLine(mapped)];

    /// <summary>One timed <see cref="OpenHandles.PidsUsing"/> line.</summary>
    /// <param name="path">The file or folder.</param>
    /// <returns>The line.</returns>
    private static string UsingLine(string path)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<int> pids = OpenHandles.PidsUsing(path);
        clock.Stop();
        return $"PidsUsing({path}): {clock.ElapsedMilliseconds} ms, pids [{PidList(pids)}]";
    }

    /// <summary>The <c>handle.exe</c> comparison, or its absence, as lines.</summary>
    /// <param name="probe">The probe folder whose rows are kept.</param>
    /// <returns>The lines.</returns>
    private static List<string> HandleExeLines(string probe)
    {
        if (ChildProcess.FindOnPath("handle.exe", Environment.GetEnvironmentVariable("PATH")) is null)
        {
            return ["handle.exe not found on PATH"];
        }
        var clock = Stopwatch.StartNew();
        string dump = new HandleExe().Dump();
        clock.Stop();
        List<HandleRow> rows = [.. HandleCsv.Parse(dump).Where(row => row.Name.StartsWith(probe, StringComparison.OrdinalIgnoreCase))];
        var lines = new List<string> { $"handle.exe: {clock.ElapsedMilliseconds} ms, {rows.Count} rows under the folder" };
        foreach (
            IGrouping<string, HandleRow> kind in rows.GroupBy(row => row.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        )
        {
            lines.Add($"  kind {kind.Key}: {kind.Count()}");
        }
        foreach (IGrouping<int, HandleRow> pid in rows.GroupBy(row => row.Pid).OrderBy(group => group.Key))
        {
            lines.Add($"  pid {pid.Key} ({pid.First().Process}): {pid.Count()}");
        }
        return lines;
    }

    /// <summary>The pids, comma-separated, or <c>none</c>.</summary>
    /// <param name="pids">The pids.</param>
    /// <returns>The text.</returns>
    private static string PidList(IReadOnlyList<int> pids) => pids.Count == 0 ? "none" : string.Join(", ", pids);

    /// <summary>The pids, comma-separated, each with the child it is or a note that it is none of them.</summary>
    /// <param name="pids">The pids.</param>
    /// <param name="labels">The probe's children, by pid.</param>
    /// <returns>The text.</returns>
    private static string LabeledPids(IReadOnlyList<int> pids, IReadOnlyDictionary<int, string> labels) =>
        pids.Count == 0 ? "none" : string.Join(", ", pids.Select(pid => $"{pid} ({Label(pid, labels)})"));

    /// <summary>Which child <paramref name="pid"/> is, or that it is not one of the probe's.</summary>
    /// <param name="pid">The pid.</param>
    /// <param name="labels">The probe's children, by pid.</param>
    /// <returns>The label.</returns>
    private static string Label(int pid, IReadOnlyDictionary<int, string> labels) =>
        labels.TryGetValue(pid, out string? label) ? label : "not a child of this probe";

    /// <summary>The positive control: a child holding <paramref name="file"/> with no share at all.</summary>
    /// <param name="file">The file to hold.</param>
    /// <param name="ready">The file the child writes once it is ready.</param>
    /// <returns>The script.</returns>
    private static string PositiveScript(string file, string ready) =>
        $"$f = [IO.File]::Open({ReadyChild.Quoted(file)}, 'Open', 'Read', 'None'); "
        + $"Set-Content -LiteralPath {ReadyChild.Quoted(ready)} ready; Start-Sleep 300";

    /// <summary>
    /// A child holding only a memory-mapped view of <paramref name="file"/>, its own file stream disposed (the six-argument
    /// <c>CreateFromFile(FileStream, string?, long, MemoryMappedFileAccess, HandleInheritability, bool)</c> with <c>leaveOpen</c>).
    /// </summary>
    /// <param name="file">The file to map.</param>
    /// <param name="ready">The file the child writes once it is ready.</param>
    /// <returns>The script.</returns>
    private static string SectionScript(string file, string ready) =>
        $"$fs = [IO.File]::Open({ReadyChild.Quoted(file)}, 'Open', 'ReadWrite', 'ReadWrite'); "
        + "$m = [IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile($fs, $null, 0, 'ReadWrite', 'None', $true); "
        + "$v = $m.CreateViewAccessor(); $fs.Dispose(); "
        + $"Set-Content -LiteralPath {ReadyChild.Quoted(ready)} ready; Start-Sleep 300";
}
