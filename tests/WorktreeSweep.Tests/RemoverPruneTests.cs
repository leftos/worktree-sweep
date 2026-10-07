using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>Pruning a registration whose folder is gone, counting the git commands through <c>GIT_TRACE</c>.</summary>
[Collection(ProcessEnvironment.Name)]
public sealed class RemoverPruneTests
{
    /// <summary>The prune action prunes the registration once: the follow-ups do not prune again.</summary>
    [Fact]
    public void PruneRegistrationPrunesOnce()
    {
        using var fx = new Fixture();
        string repo = fx.Repo("repo");
        string wt = fx.PathTo(@"repo.wt\feat");
        Fixture.AddWorktree(repo, wt, "feat");
        Directory.Delete(wt, recursive: true);
        RegisteredCandidate registered = Assert.IsType<RegisteredCandidate>(
            Assert.Single(fx.Scan().Candidates, candidate => Fixture.SamePath(candidate.Path, wt))
        );
        Assert.NotNull(registered.Record.Prunable);
        string trace = fx.PathTo("git-trace.txt");
        Decision decision = new(registered, new Plan.Run(new RemoveAction.PruneRegistration()), new BranchChoice.NotOffered());

        IReadOnlyList<Swept> swept;
        using (new InheritedEnv(new Dictionary<string, string> { ["GIT_TRACE"] = trace }))
        {
            swept = Remover.RemovePicks([decision], _ => { }, _ => UnlockOutcome.Skipped, TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Outcome.Pruned(), swept[0].Outcome);
        Assert.Empty(swept[0].Notes);
        Assert.DoesNotContain("repo.wt/feat", Fixture.Git(repo, ["worktree", "list", "--porcelain"]), StringComparison.Ordinal);
        string[] prunes = [.. File.ReadAllLines(trace).Where(line => line.Contains("built-in: git worktree prune", StringComparison.Ordinal))];
        Assert.True(prunes.Length == 1, $"git worktree prune ran {prunes.Length} times:\n{string.Join('\n', prunes)}");
    }
}
