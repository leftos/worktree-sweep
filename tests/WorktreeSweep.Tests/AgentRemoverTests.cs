using System.Text.Json;
using WorktreeSweep.Agent;
using WorktreeSweep.Discovery;
using WorktreeSweep.Holders;
using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;

namespace WorktreeSweep.Tests;

/// <summary>An agent's removal of one worktree, its report and the report's JSON.</summary>
public sealed class AgentRemoverTests
{
    private static readonly AgentOptions Plain = new(Force: false, StopBuildServers: false);

    private static readonly BinCapacity Fits = new(uint.MaxValue, NukeOnDelete: false);

    /// <summary>A repo's main worktree is refused, and nothing is touched.</summary>
    [Fact]
    public void RefusesTheMainWorktree()
    {
        using var fx = new Fixture();
        (string repo, _) = RepoWithWorktree(fx, @"x.wt\feat", "feat");

        RemoveReport report = AgentRemover.Run(repo, Plain, Seams(NeverRecycle, NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Refused, report.Status);
        Assert.Equal(Reason.NotRemovable(RefusalReason.MainWorktree), report.Reason);
        Assert.True(File.Exists(Path.Join(repo, "README.md")));
    }

    /// <summary>A worktree with an untracked file is refused as one whose removal would lose work, and the file stays.</summary>
    [Fact]
    public void RefusesADirtyWorktreeWithoutForce()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        File.WriteAllText(Path.Join(worktree, "scratch.txt"), "unsaved\n");

        RemoveReport report = AgentRemover.Run(worktree, Plain, Seams(NeverRecycle, NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Refused, report.Status);
        Assert.Equal(Reason.WouldLose, report.Reason);
        Assert.NotNull(report.Loss);
        Assert.Contains("1 untracked file", report.Loss, StringComparison.Ordinal);
        Assert.EndsWith("will be lost.", report.Loss, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(worktree, "scratch.txt")), "the worktree was touched");
    }

    /// <summary>A registered worktree whose folder is gone is pruned, and its branch, with no commits of its own, deleted.</summary>
    [Fact]
    public void RemovesAPrunableRecord()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        Directory.Delete(worktree, recursive: true);

        RemoveReport report = AgentRemover.Run(worktree, Plain, Seams(NeverRecycle, NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Removed, report.Status);
        Assert.Null(report.Reason);
        Assert.True(report.BranchDeleted, string.Join("; ", report.Notes));
        Assert.DoesNotContain("feat", WorktreeList(repo), StringComparison.Ordinal);
        Assert.Empty(Fixture.Git(repo, ["branch", "--list", "feat"]));
    }

    /// <summary>A worktree beside its repo (<c>x-feat</c> next to <c>x</c>), its folder deleted, is found through the sibling repo.</summary>
    [Fact]
    public void RemovesAPrunableSiblingWorktree()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, "x-feat", "feat");
        Directory.Delete(worktree, recursive: true);

        RemoveReport report = AgentRemover.Run(worktree, Plain, Seams(NeverRecycle, NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Removed, report.Status);
        Assert.True(report.BranchDeleted, string.Join("; ", report.Notes));
        Assert.DoesNotContain("x-feat", WorktreeList(repo), StringComparison.Ordinal);
        Assert.Empty(Fixture.Git(repo, ["branch", "--list", "feat"]));
    }

    /// <summary>
    /// A worktree folder whose <c>.git</c> file is gone is prunable to git but still on disk: it is refused as an orphan, and neither
    /// the record nor the branch is touched.
    /// </summary>
    [Fact]
    public void RefusesAPrunableRecordWhoseFolderRemains()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x\.claude\worktrees\a", "a");
        File.Delete(Path.Join(worktree, ".git"));

        RemoveReport report = AgentRemover.Run(worktree, Plain, Seams(NeverRecycle, NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Refused, report.Status);
        Assert.Equal(Reason.NotRemovable(RefusalReason.Orphan), report.Reason);
        Assert.True(File.Exists(Path.Join(worktree, "README.md")), "the folder was touched");
        Assert.Contains(".claude/worktrees/a", WorktreeList(repo), StringComparison.Ordinal);
        Assert.NotEmpty(Fixture.Git(repo, ["branch", "--list", "a"]));
    }

    /// <summary>A recycle that does not finish within the timeout releases the worktree and writes its marker.</summary>
    [Fact]
    public void ATimedOutRecycleIsReleasedNotRemoved()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");

        // The abandoned recycle thread sleeps on after the run returns, touching nothing.
        RemoveReport report = AgentRemover.Run(worktree, Plain, Seams(_ => Thread.Sleep(TimeSpan.FromSeconds(5)), NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Released, report.Status);
        Assert.Equal(Reason.ShellTimeout, report.Reason);
        Assert.NotNull(report.Released);
        Assert.Equal(Reason.ShellTimeout, report.Released.Reason);
        Released? marker = ReleasedMarker.Read(admin);
        Assert.NotNull(marker);
        Assert.Equal(Reason.ShellTimeout, marker.Reason);
        Assert.Equal(report.Released.ReleasedAtUnix, marker.ReleasedAtUnix);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>
    /// A recycle that moves the folder away and then outlives the timeout, as the Shell does on a large tree, is reported removed: the
    /// record is pruned, the branch deleted, and no released marker is left.
    /// </summary>
    [Fact]
    public void ATimedOutRecycleThatAlreadyMovedTheFolderIsRemoved()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");
        string bin = fx.PathTo("bin-entry");

        void MoveThenLinger(string path)
        {
            Directory.Move(path, bin);
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }

        RemoveReport report = AgentRemover.Run(worktree, Plain, Seams(MoveThenLinger, NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Removed, report.Status);
        Assert.Null(report.Reason);
        Assert.Null(report.Released);
        Assert.True(report.BranchDeleted, string.Join("; ", report.Notes));
        Assert.Contains("the move to the Recycle Bin finished after the recycle timed out", report.Notes);
        Assert.DoesNotContain("feat", WorktreeList(repo), StringComparison.Ordinal);
        Assert.False(Directory.Exists(admin), "the record was not pruned");
    }

    /// <summary>
    /// A completed recycle whose repo cannot be pruned afterwards, the folder already gone, still reports the worktree removed, with the
    /// prune and branch failures as notes.
    /// </summary>
    [Fact]
    public void RecycleThenBrokenRepoReportsRemovedWithPruneAndBranchNotes()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");

        void DeleteFolderThenBreakRepo(string path)
        {
            Directory.Delete(path, recursive: true);
            Directory.Move(Path.Join(repo, ".git"), Path.Join(repo, ".git-broken"));
        }

        RemoveReport report = AgentRemover.Run(worktree, Plain, Seams(DeleteFolderThenBreakRepo, NoHolders, NeverStop));

        Assert.Equal(RemoveStatus.Removed, report.Status);
        Assert.False(report.BranchDeleted, string.Join("; ", report.Notes));
        Assert.Single(report.Notes, note => note.StartsWith("prune failed:", StringComparison.Ordinal));
        Assert.Single(report.Notes, note => note.StartsWith("branch feat kept:", StringComparison.Ordinal));
        Assert.False(Directory.Exists(worktree), "the worktree folder is still there");
    }

    /// <summary>
    /// A locked recycle with <c>--stop-build-servers</c> stops the allowlisted holder, retries once, and removes the worktree when the
    /// retry goes through.
    /// </summary>
    [Fact]
    public void ALockedRecycleWithStopBuildServersStopsAllowlistedHoldersAndRetries()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        var cargo = new Holder(4242, "cargo.exe", null, 0, null, [new Hold.OpenHandle(Path.Join(worktree, "README.md"))]);
        int recycles = 0;
        var stopped = new List<Holder>();
        void Recycle(string path)
        {
            recycles++;
            if (recycles == 1)
            {
                throw new LockedException(path, firstLockedFile: null);
            }
            Directory.Delete(path, recursive: true);
        }

        RemoveReport report = AgentRemover.Run(
            worktree,
            new AgentOptions(Force: false, StopBuildServers: true),
            Seams(Recycle, (_, _) => new HolderReport([cargo], []), stopped.Add) with
            {
                StillSame = _ => true,
            }
        );

        Assert.Equal(RemoveStatus.Removed, report.Status);
        Assert.Equal(2, recycles);
        Assert.Equal([cargo], stopped);
        Assert.Equal([new ProcessRef { Pid = 4242, Exe = "cargo.exe" }], report.Stopped);
        Assert.Empty(report.Holders);
        Assert.True(report.BranchDeleted, string.Join("; ", report.Notes));
        Assert.DoesNotContain("feat", WorktreeList(repo), StringComparison.Ordinal);
    }

    /// <summary>An allowlisted holder whose PID may now name another process is never stopped, and the run says why.</summary>
    [Fact]
    public void StopBuildServersSkipsAHolderThatIsNoLongerTheSame()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        var cargo = new Holder(4242, "cargo.exe", null, 0, null, [new Hold.OpenHandle(Path.Join(worktree, "README.md"))]);
        var stopped = new List<Holder>();

        RemoveReport report = AgentRemover.Run(
            worktree,
            new AgentOptions(Force: false, StopBuildServers: true),
            Seams(AlwaysLocked, (_, _) => new HolderReport([cargo], []), stopped.Add) with
            {
                StillSame = _ => false,
            }
        );

        Assert.Equal(RemoveStatus.Released, report.Status);
        Assert.Empty(stopped);
        Assert.Empty(report.Stopped);
        Assert.Contains("pid 4242 (cargo.exe) not stopped: it is no longer the same process", report.Notes);
    }

    /// <summary>A worktree bigger than its volume's Recycle Bin is released without a recycle.</summary>
    [Fact]
    public void TooBigForTheBinIsReleased()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");

        RemoveReport report = AgentRemover.Run(
            worktree,
            Plain,
            Seams(NeverRecycle, NoHolders, NeverStop) with
            {
                ReadCapacity = _ => new BinCapacity(0, NukeOnDelete: false),
            }
        );

        Assert.Equal(RemoveStatus.Released, report.Status);
        Assert.Equal(Reason.TooBigForRecycleBin, report.Reason);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>A holder scan refused on the retry is a note, and the locked worktree is released.</summary>
    [Fact]
    public void RetryNotesAHolderScanThatIsRefused()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");

        // The caller's own scan excludes this process; the retry's scan excludes nothing, and that one is refused.
        RemoveReport report = AgentRemover.Run(
            worktree,
            Plain,
            Seams(
                AlwaysLocked,
                (_, exclude) => exclude.Count == 0 ? throw new UnauthorizedAccessException("access denied") : new HolderReport([], []),
                NeverStop
            )
        );

        Assert.Equal(RemoveStatus.Released, report.Status);
        Assert.Contains(report.Notes, note => note.StartsWith("cannot list the processes holding it:", StringComparison.Ordinal));
    }

    /// <summary>A git lock <c>--force</c> lifted is put back, with its reason, when the worktree stays and is released.</summary>
    [Fact]
    public void ForceRelocksAWorktreeItReleases()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        _ = Fixture.Git(repo, ["worktree", "lock", "--reason", "held by test", worktree]);

        RemoveReport report = AgentRemover.Run(
            worktree,
            new AgentOptions(Force: true, StopBuildServers: false),
            Seams(AlwaysLocked, NoHolders, NeverStop)
        );

        Assert.Equal(RemoveStatus.Released, report.Status);
        Assert.Contains("locked held by test", WorktreeList(repo), StringComparison.Ordinal);
    }

    /// <summary>A holder the run stopped leaves the report and the marker; a holder that could not be stopped stays in both.</summary>
    [Fact]
    public void StoppedHoldersAreNotReportedAsHolders()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        string admin = Discoverer.ReadGitdirFile(worktree) ?? throw new InvalidOperationException($"{worktree} has no admin dir");
        var cargo = new Holder(4242, "cargo.exe", null, 0, null, [new Hold.OpenHandle(Path.Join(worktree, "README.md"))]);
        var code = new Holder(99, "code.exe", null, 0, null, [new Hold.OpenHandle(Path.Join(worktree, "README.md"))]);
        var stopped = new List<Holder>();

        RemoveReport report = AgentRemover.Run(
            worktree,
            new AgentOptions(Force: false, StopBuildServers: true),
            Seams(AlwaysLocked, (_, _) => new HolderReport([cargo, code], []), stopped.Add) with
            {
                StillSame = _ => true,
            }
        );

        Assert.Equal(RemoveStatus.Released, report.Status);
        Assert.Equal(Reason.Locked, report.Reason);
        Assert.Equal([cargo], stopped);
        Assert.Equal([new ProcessRef { Pid = 4242, Exe = "cargo.exe" }], report.Stopped);
        Assert.Equal([code], report.Holders);
        Released? marker = ReleasedMarker.Read(admin);
        Assert.NotNull(marker);
        Assert.Equal([new ProcessRef { Pid = 99, Exe = "code.exe" }], marker.Holders);
    }

    /// <summary>A recycle failure that is not a lock puts back the git lock <c>--force</c> lifted before it leaves the run.</summary>
    [Fact]
    public void RecycleFailureRestoresTheGitLock()
    {
        using var fx = new Fixture();
        (string repo, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        _ = Fixture.Git(repo, ["worktree", "lock", "--reason", "held by test", worktree]);
        var failure = new IOException("disk on fire");

        IOException error = Assert.Throws<IOException>(() =>
        {
            _ = AgentRemover.Run(worktree, new AgentOptions(Force: true, StopBuildServers: false), Seams(_ => throw failure, NoHolders, NeverStop));
        });

        Assert.Same(failure, error);
        Assert.Contains("locked held by test", WorktreeList(repo), StringComparison.Ordinal);
    }

    /// <summary>A recycle failure on the retry names the processes the run stopped before it failed.</summary>
    [Fact]
    public void RecycleFailureOnTheRetryNamesTheStoppedProcesses()
    {
        using var fx = new Fixture();
        (_, string worktree) = RepoWithWorktree(fx, @"x.wt\feat", "feat");
        var cargo = new Holder(4242, "cargo.exe", null, 0, null, [new Hold.OpenHandle(Path.Join(worktree, "README.md"))]);
        var failure = new IOException("disk on fire");
        int recycles = 0;
        void Recycle(string path)
        {
            recycles++;
            if (recycles == 1)
            {
                throw new LockedException(path, firstLockedFile: null);
            }
            throw failure;
        }

        IOException error = Assert.Throws<IOException>(() =>
        {
            _ = AgentRemover.Run(
                worktree,
                new AgentOptions(Force: false, StopBuildServers: true),
                Seams(Recycle, (_, _) => new HolderReport([cargo], []), _ => { }) with
                {
                    StillSame = _ => true,
                }
            );
        });

        Assert.Equal(2, recycles);
        Assert.Equal("disk on fire; stopped before the failure: pid 4242 (cargo.exe)", error.Message);
        IOException inner = Assert.IsType<IOException>(error.InnerException);
        Assert.Same(failure, inner);
        Assert.Equal("disk on fire", inner.Message);
    }

    /// <summary>A report with every field set serialises to the spec's fields, in order; a refused report writes its absent fields as null.</summary>
    [Fact]
    public void ReportJsonHasTheSpecFields()
    {
        const string Expected = """
            {
              "status": "released",
              "reason": "locked",
              "path": "D:\\x.wt\\feat",
              "repo": "D:\\x",
              "branch": "feat",
              "branch_deleted": true,
              "loss": "x.wt\\feat: 1 untracked file will be lost.",
              "cd_to": "D:\\x",
              "holders": [
                {
                  "pid": 42,
                  "exe": "cargo.exe",
                  "image": "C:\\tools\\cargo.exe",
                  "started": "2026-09-21T14:13:20Z",
                  "command_line": "cargo build",
                  "holds": [
                    {
                      "kind": "current_folder",
                      "path": "D:\\x.wt\\feat"
                    },
                    {
                      "kind": "open_handle",
                      "path": "D:\\x.wt\\feat\\target\\.lock"
                    }
                  ]
                },
                {
                  "pid": 50,
                  "exe": "code.exe",
                  "image": null,
                  "started": null,
                  "command_line": null,
                  "holds": []
                }
              ],
              "may_hold": [
                {
                  "pid": 77,
                  "exe": "svchost.exe",
                  "why": "unnamed_handle"
                }
              ],
              "stopped": [
                {
                  "pid": 43,
                  "exe": "rust-analyzer.exe"
                }
              ],
              "released": {
                "released_at": "2026-09-21T14:13:20Z",
                "reason": "locked",
                "holders": [
                  {
                    "pid": 42,
                    "exe": "cargo.exe"
                  }
                ]
              },
              "notes": [
                "branch feat kept: squash-merged, delete it with git branch -D"
              ]
            }
            """;
        const ulong Filetime = 134_344_736_000_000_000;
        RemoveReport report = Refused(RemoveStatus.Released, Reason.Locked) with
        {
            Path = @"D:\x.wt\feat",
            Repo = @"D:\x",
            Branch = "feat",
            BranchDeleted = true,
            Loss = @"x.wt\feat: 1 untracked file will be lost.",
            CdTo = @"D:\x",
            Holders =
            [
                new Holder(
                    42,
                    "cargo.exe",
                    @"C:\tools\cargo.exe",
                    Filetime,
                    "cargo build",
                    [new Hold.CurrentFolder(@"D:\x.wt\feat"), new Hold.OpenHandle(@"D:\x.wt\feat\target\.lock")]
                ),
                new Holder(50, "code.exe", null, 0, null, []),
            ],
            MayHold = [new MayHold(77, "svchost.exe", MayHoldWhy.UnnamedHandle)],
            Stopped = [new ProcessRef { Pid = 43, Exe = "rust-analyzer.exe" }],
            Released = new Released
            {
                ReleasedAtUnix = 1_790_000_000,
                Reason = Reason.Locked,
                Holders = [new ProcessRef { Pid = 42, Exe = "cargo.exe" }],
            },
            Notes = ["branch feat kept: squash-merged, delete it with git branch -D"],
        };

        Assert.Equal(Expected + "\n", Json(report));

        const string ExpectedRefused = """
            {
              "status": "refused",
              "reason": "main_worktree",
              "path": "D:\\x",
              "repo": null,
              "branch": null,
              "branch_deleted": false,
              "loss": null,
              "cd_to": null,
              "holders": [],
              "may_hold": [],
              "stopped": [],
              "released": null,
              "notes": []
            }
            """;
        Assert.Equal(ExpectedRefused + "\n", Json(Refused(RemoveStatus.Refused, Reason.NotRemovable(RefusalReason.MainWorktree))));
    }

    /// <summary>Every status serialises as its snake_case string.</summary>
    /// <param name="status">The status.</param>
    /// <param name="expected">Its JSON string.</param>
    [Theory]
    [InlineData(RemoveStatus.Removed, "removed")]
    [InlineData(RemoveStatus.Released, "released")]
    [InlineData(RemoveStatus.Refused, "refused")]
    public void StatusSerialisesAsSnakeCase(RemoveStatus status, string expected)
    {
        using var document = JsonDocument.Parse(Json(Refused(status, null)));

        Assert.Equal(expected, document.RootElement.GetProperty("status").GetString());
    }

    /// <summary>Every reason a report can carry serialises as its snake_case string; a refusal as its own name.</summary>
    /// <param name="kind">The reason's kind.</param>
    /// <param name="refusal">For <see cref="ReasonKind.NotRemovable"/>, why.</param>
    /// <param name="expected">Its JSON string.</param>
    [Theory]
    [InlineData(ReasonKind.Locked, null, "locked")]
    [InlineData(ReasonKind.MayHold, null, "may_hold")]
    [InlineData(ReasonKind.TooBigForRecycleBin, null, "too_big_for_recycle_bin")]
    [InlineData(ReasonKind.ShellTimeout, null, "shell_timeout")]
    [InlineData(ReasonKind.WouldLose, null, "would_lose")]
    [InlineData(ReasonKind.CallerHolds, null, "caller_holds")]
    [InlineData(ReasonKind.NotRemovable, RefusalReason.NotFound, "not_found")]
    [InlineData(ReasonKind.NotRemovable, RefusalReason.NotAWorktree, "not_a_worktree")]
    [InlineData(ReasonKind.NotRemovable, RefusalReason.MainWorktree, "main_worktree")]
    [InlineData(ReasonKind.NotRemovable, RefusalReason.BareRepo, "bare_repo")]
    [InlineData(ReasonKind.NotRemovable, RefusalReason.Subfolder, "subfolder")]
    [InlineData(ReasonKind.NotRemovable, RefusalReason.Link, "link")]
    [InlineData(ReasonKind.NotRemovable, RefusalReason.Orphan, "orphan")]
    public void ReasonSerialisesAsSnakeCase(ReasonKind kind, RefusalReason? refusal, string expected)
    {
        Reason reason = kind switch
        {
            ReasonKind.Locked => Reason.Locked,
            ReasonKind.MayHold => Reason.MayHold,
            ReasonKind.TooBigForRecycleBin => Reason.TooBigForRecycleBin,
            ReasonKind.ShellTimeout => Reason.ShellTimeout,
            ReasonKind.WouldLose => Reason.WouldLose,
            ReasonKind.CallerHolds => Reason.CallerHolds,
            _ => Reason.NotRemovable(refusal ?? throw new ArgumentNullException(nameof(refusal))),
        };

        using var document = JsonDocument.Parse(Json(Refused(RemoveStatus.Refused, reason)));

        Assert.Equal(expected, document.RootElement.GetProperty("reason").GetString());
    }

    private static void NeverRecycle(string path) => throw new InvalidOperationException($"{path} must not be recycled");

    private static void AlwaysLocked(string path) => throw new LockedException(path, firstLockedFile: null);

    private static HolderReport NoHolders(string folder, IReadOnlyCollection<int> exclude) => new([], []);

    private static void NeverStop(Holder holder) => throw new InvalidOperationException($"pid {holder.Pid} must not be stopped");

    private static bool NeverChecked(Holder holder) => throw new InvalidOperationException($"pid {holder.Pid} must not be checked");

    /// <summary>
    /// Test seams: a 200 ms recycle timeout, the caller's chain this process alone, the current folder left alone, and a Recycle Bin
    /// that takes anything, so no test reads the real one.
    /// </summary>
    private static AgentSeams Seams(Action<string> recycle, Func<string, IReadOnlyCollection<int>, HolderReport> findHolders, Action<Holder> stop) =>
        new()
        {
            EnterMainWorktree = _ => { },
            Recycle = recycle,
            RecycleTimeout = TimeSpan.FromMilliseconds(200),
            FindHolders = findHolders,
            OwnChain = () => new HashSet<int> { Environment.ProcessId },
            StillSame = NeverChecked,
            Stop = stop,
            ReadCapacity = _ => Fits,
        };

    /// <summary>A repo <c>x</c> under the fixture root with a clean worktree at <paramref name="relative"/> on a new branch.</summary>
    private static (string Repo, string Worktree) RepoWithWorktree(Fixture fx, string relative, string branch)
    {
        string repo = fx.Repo("x");
        string worktree = fx.PathTo(relative);
        Fixture.AddWorktree(repo, worktree, branch);
        return (repo, worktree);
    }

    private static string WorktreeList(string repo) => Fixture.Git(repo, ["worktree", "list", "--porcelain"]);

    /// <summary>A report on <c>D:\x</c> with <paramref name="status"/> and <paramref name="reason"/>, every other field empty.</summary>
    private static RemoveReport Refused(RemoveStatus status, Reason? reason) =>
        new()
        {
            Status = status,
            Reason = reason,
            Path = @"D:\x",
            Repo = null,
            Branch = null,
            BranchDeleted = false,
            Loss = null,
            CdTo = null,
            Holders = [],
            MayHold = [],
            Stopped = [],
            Released = null,
            Notes = [],
        };

    private static string Json(RemoveReport report)
    {
        using var output = new StringWriter();
        RemoveReportJson.Write(report, output);
        return output.ToString();
    }
}
