using System.Text;
using System.Text.Json;
using WorktreeSweep.Discovery;
using WorktreeSweep.Report;
using WorktreeSweep.Signals;
using static WorktreeSweep.Tests.ReportSamples;

namespace WorktreeSweep.Tests;

/// <summary>The scan report's JSON form, as <c>--json</c> prints it.</summary>
public sealed class ReportJsonTests
{
    /// <summary>A released worktree carries its marker; any other carries <c>"released": null</c>.</summary>
    [Fact]
    public void WriteJsonCarriesTheReleasedMarker()
    {
        string json = Json(ReportOf(Released("yaat", @"yaat.wt\held")));
        Assert.Contains("\"released\": {", json, StringComparison.Ordinal);
        Assert.Contains("\"reason\": \"locked\"", json, StringComparison.Ordinal);

        string plain = Json(ReportOf(Registered(@"yaat.wt\plain", null, new WorktreeSignals())));
        Assert.Contains("\"released\": null", plain, StringComparison.Ordinal);
    }

    /// <summary>A branch with no commits of its own reads <c>no commits</c> in the table and <c>no_commits</c> in the JSON.</summary>
    [Fact]
    public void NoCommitsBranchShowsInTableAndJson()
    {
        var signals = new WorktreeSignals
        {
            MergeState = MergeState.NoCommits,
            MergeStateAgainst = "main",
            Dirty = new Dirty(),
            LastActivityUnix = Now - (3 * Day),
            Size = Size(1536, Now),
        };
        ScanReport report = ReportOf(Registered(@"yaat.wt\eram-co\yaat", "eram-co", signals));

        string row = ReportTable.Render(report, Now).Split('\n')[1];
        Assert.Contains("eram-co  no commits", row, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"no_commits\"", Json(report), StringComparison.Ordinal);
    }

    /// <summary>A signal that could not be read is <c>null</c> and its message is in <c>errors</c>.</summary>
    [Fact]
    public void UnreadSignalIsNullAndListedInErrors()
    {
        var signals = new WorktreeSignals
        {
            MergeState = MergeState.Ancestor,
            MergeStateAgainst = "main",
            Upstream = Upstream.None,
            LastActivityUnix = Now,
            Size = Size(10, Now),
            Errors = ["cannot read the dirty state: git status failed"],
        };

        using var document = JsonDocument.Parse(Json(ReportOf(Registered(@"yaat.wt\bad", "bad", signals))));

        JsonElement candidate = document.RootElement.GetProperty("candidates")[0];
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("dirty").ValueKind);
        string?[] errors = [.. candidate.GetProperty("errors").EnumerateArray().Select(error => error.GetString())];
        Assert.Equal(["cannot read the dirty state: git status failed"], errors);
    }

    /// <summary>A time outside what a timestamp can hold is written as <c>null</c>, and the rest of the document still is.</summary>
    [Fact]
    public void OutOfRangeTimeIsWrittenAsNull()
    {
        var signals = new WorktreeSignals { LastActivityUnix = long.MaxValue, Size = Size(10, long.MinValue) };

        string json = Json(ReportOf(Registered(@"yaat.wt\far", "far", signals)));

        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        JsonElement candidate = Assert.Single(document.RootElement.GetProperty("candidates").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("last_activity").ValueKind);
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("size").GetProperty("last_write").ValueKind);
        Assert.Equal(JsonValueKind.Array, candidate.GetProperty("errors").ValueKind);
    }

    /// <summary>Each merge state and upstream state is an object tagged by <c>state</c>, with its count or flag where it has one.</summary>
    [Fact]
    public void MergeStatesAndUpstreamsAreTaggedObjects()
    {
        (MergeState State, string Json)[] merges =
        [
            (MergeState.Ancestor, """{"state":"ancestor"}"""),
            (MergeState.NoCommits, """{"state":"no_commits"}"""),
            (MergeState.PatchesApplied, """{"state":"patches_applied"}"""),
            (MergeState.ContentContained, """{"state":"content_contained"}"""),
            (MergeState.Unmerged(4), """{"state":"unmerged","commits":4}"""),
            (MergeState.Detached(true), """{"state":"detached","contained":true}"""),
        ];
        (Upstream State, string Json)[] upstreams =
        [
            (Upstream.None, """{"state":"none"}"""),
            (Upstream.Gone, """{"state":"gone"}"""),
            (Upstream.Tracking(2), """{"state":"tracking","ahead":2}"""),
        ];

        foreach ((MergeState State, string Json) merge in merges)
        {
            Assert.Equal(merge.Json, Field(new WorktreeSignals { MergeState = merge.State }, "merge_state"));
        }
        foreach ((Upstream State, string Json) upstream in upstreams)
        {
            Assert.Equal(upstream.Json, Field(new WorktreeSignals { Upstream = upstream.State }, "upstream"));
        }
    }

    /// <summary>
    /// The whole document: snake_case keys, the <c>kind</c> tag first, signals flattened, <c>errors</c> always present, nulls written,
    /// ISO 8601 UTC timestamps, relaxed escaping, <c>\n</c> line ends, UTF-8 without a BOM and a trailing newline.
    /// </summary>
    [Fact]
    public void WriteJsonWritesTheWholeDocument()
    {
        var signals = new WorktreeSignals
        {
            MergeState = MergeState.Unmerged(4),
            MergeStateAgainst = "main",
            Dirty = new Dirty { Modified = 3, Untracked = 2 },
            Upstream = Upstream.Tracking(2),
            LastActivityUnix = Now - (3 * Day),
            Size = Size(1536, Now),
        };
        RegisteredCandidate held = Released("yaat", @"yaat.wt\held") with { Signals = signals };
        held = held with
        {
            Record = held.Record with { Locked = "on Bob's USB drive" },
            Released = held.Released! with { Holders = [new ProcessRef { Pid = 42, Exe = "devenv.exe" }] },
        };
        OrphanCandidate link = Orphan(@"yaat-server.wt\yaat", OrphanKind.Link, Size(0, Now - (2 * 3600)) with { Files = 0 });
        link = link with { Orphan = link.Orphan with { Container = Under("yaat-server.wt"), LinkTarget = Under("yaat-server") } };
        ScanReport report = ReportOf(held, link) with
        {
            Repos =
            [
                new RepoReport
                {
                    Path = Under("yaat"),
                    DefaultBranches = new DefaultBranches { Local = "main" },
                },
            ],
        };

        using var stream = new MemoryStream();
        ReportJson.Write(report, stream);
        byte[] bytes = stream.ToArray();

        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal(GoldenDocument.ReplaceLineEndings("\n") + "\n", Encoding.UTF8.GetString(bytes));
    }

    private const string GoldenDocument = """
        {
          "root": "D:\\",
          "repos": [
            {
              "path": "D:\\yaat",
              "default_branches": {
                "local": "main",
                "origin": null
              }
            }
          ],
          "candidates": [
            {
              "kind": "registered",
              "path": "D:\\yaat.wt\\held",
              "repo": "D:\\yaat",
              "branch": "feat",
              "head": "0123456789abcdef",
              "prunable": null,
              "git_lock": "on Bob's USB drive",
              "released": {
                "released_at": "2026-09-21T14:13:20Z",
                "reason": "locked",
                "holders": [
                  {
                    "pid": 42,
                    "exe": "devenv.exe"
                  }
                ]
              },
              "merge_state": {
                "state": "unmerged",
                "commits": 4
              },
              "merge_state_against": "main",
              "dirty": {
                "modified": 3,
                "untracked": 2
              },
              "upstream": {
                "state": "tracking",
                "ahead": 2
              },
              "last_activity": "2026-09-18T14:13:20Z",
              "size": {
                "bytes": 1536,
                "files": 1,
                "unreadable": 0,
                "last_write": "2026-09-21T14:13:20Z"
              },
              "errors": []
            },
            {
              "kind": "orphan",
              "path": "D:\\yaat-server.wt\\yaat",
              "container": "D:\\yaat-server.wt",
              "orphan_kind": "link",
              "link_target": "D:\\yaat-server",
              "stale_gitdir": false,
              "live_gitdir": null,
              "has_git_dir": false,
              "size": {
                "bytes": 0,
                "files": 0,
                "unreadable": 0,
                "last_write": "2026-09-21T12:13:20Z"
              }
            }
          ]
        }
        """;

    private static string Json(ScanReport report)
    {
        using var stream = new MemoryStream();
        ReportJson.Write(report, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>One field of the only candidate of a report holding one worktree with <paramref name="signals"/>, as compact JSON.</summary>
    private static string Field(WorktreeSignals signals, string name)
    {
        using var document = JsonDocument.Parse(Json(ReportOf(Registered(@"yaat.wt\one", "one", signals))));
        return JsonSerializer.Serialize(document.RootElement.GetProperty("candidates")[0].GetProperty(name));
    }
}
