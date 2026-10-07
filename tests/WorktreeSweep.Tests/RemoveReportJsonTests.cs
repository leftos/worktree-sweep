using WorktreeSweep.Agent;
using WorktreeSweep.Holders;
using WorktreeSweep.Report;

namespace WorktreeSweep.Tests;

/// <summary>An agent removal's report as the JSON document <c>remove --json</c> prints.</summary>
public sealed class RemoveReportJsonTests
{
    /// <summary>A holder's start time: 2023-11-14T21:56:40Z as a FILETIME.</summary>
    private const ulong HolderStarted = 133_444_726_000_000_000;

    /// <summary>
    /// A released report with a holder, a may-hold, a stopped process, a released record and notes is written key for key as the
    /// golden document: snake_case, <c>null</c> for absent fields, times as ISO 8601 UTC strings, and a trailing <c>\n</c>.
    /// </summary>
    [Fact]
    public void RemoveReportJsonMatchesTheGolden()
    {
        var report = new RemoveReport
        {
            Status = RemoveStatus.Released,
            Reason = Reason.Locked,
            Path = @"D:\x.wt\feat",
            Repo = @"D:\x",
            Branch = "feat",
            BranchDeleted = false,
            Loss = null,
            CdTo = null,
            Holders =
            [
                new Holder(
                    4242,
                    "pwsh.exe",
                    @"C:\Program Files\PowerShell\7\pwsh.exe",
                    HolderStarted,
                    "pwsh -NoProfile",
                    [new Hold.CurrentFolder(@"D:\x.wt\feat")]
                ),
            ],
            MayHold = [new MayHold(77, "MsMpEng.exe", MayHoldWhy.UnnamedHandle)],
            Stopped = [new ProcessRef { Pid = 88, Exe = "dotnet.exe" }],
            Released = new Released
            {
                ReleasedAtUnix = 1_700_000_000,
                Reason = Reason.Locked,
                Holders = [new ProcessRef { Pid = 4242, Exe = "pwsh.exe" }],
            },
            Notes = ["pwsh.exe (pid 4242) still holds it."],
        };

        using var output = new StringWriter();
        RemoveReportJson.Write(report, output);

        Assert.Equal(GoldenDocument.ReplaceLineEndings("\n") + "\n", output.ToString());
    }

    private const string GoldenDocument = """
        {
          "status": "released",
          "reason": "locked",
          "path": "D:\\x.wt\\feat",
          "repo": "D:\\x",
          "branch": "feat",
          "branch_deleted": false,
          "loss": null,
          "cd_to": null,
          "holders": [
            {
              "pid": 4242,
              "exe": "pwsh.exe",
              "image": "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
              "started": "2023-11-14T21:56:40Z",
              "command_line": "pwsh -NoProfile",
              "holds": [
                {
                  "kind": "current_folder",
                  "path": "D:\\x.wt\\feat"
                }
              ]
            }
          ],
          "may_hold": [
            {
              "pid": 77,
              "exe": "MsMpEng.exe",
              "why": "unnamed_handle"
            }
          ],
          "stopped": [
            {
              "pid": 88,
              "exe": "dotnet.exe"
            }
          ],
          "released": {
            "released_at": "2023-11-14T22:13:20Z",
            "reason": "locked",
            "holders": [
              {
                "pid": 4242,
                "exe": "pwsh.exe"
              }
            ]
          },
          "notes": [
            "pwsh.exe (pid 4242) still holds it."
          ]
        }
        """;
}
