using WorktreeSweep.Recycle;
using WorktreeSweep.Removal;
using WorktreeSweep.Report;
using WorktreeSweep.Review;
using WorktreeSweep.Signals;
using WorktreeSweep.ViewModels;

namespace WorktreeSweep.Tests.ViewModels;

/// <summary>The Review screen: the capacity read, the questions, the kept answers and the final confirmation.</summary>
public sealed class ReviewViewModelTests
{
    /// <summary>Entering Review waits for the capacities of the ticked folders, and of no link.</summary>
    [Fact]
    public void ReviewStartsPreparingWithTheCapacityPaths()
    {
        RegisteredCandidate folder = ViewModelSamples.Worktree("a", MergeState.Ancestor, 10);
        MainViewModel main = ViewModelSamples.Listed(folder, ViewModelSamples.Link("link"));
        ViewModelSamples.ListOf(main).TickAllCommand.Execute(null);

        ViewModelSamples.ListOf(main).ReviewCommand.Execute(null);

        Assert.Equal(Screen.Review, main.Screen);
        ReviewViewModel? review = main.Review;
        Assert.NotNull(review);
        Assert.Equal(ReviewState.Preparing, review.State);
        Assert.Equal([folder.Path], review.CapacityPaths);
    }

    /// <summary>The capacities build the review, which asks its first question.</summary>
    [Fact]
    public void CapacitiesReadAsksTheFirstStep()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));

        ReviewViewModel review = ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);

        Assert.Equal(ReviewState.Asking, review.State);
        Assert.Equal("Delete branch", review.Title);
        Assert.Equal(@"repo.wt\a", review.PickPath);
        Assert.StartsWith("Branch a: ", review.Body, StringComparison.Ordinal);
        Assert.Equal("Delete the branch?", review.Question);
        Assert.True(review.DefaultAnswer);
    }

    /// <summary>A capacity reply after Back, or for other paths than the review asked for, changes nothing.</summary>
    [Fact]
    public void StaleCapacitiesReadIsIgnored()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
        ListViewModel list = ViewModelSamples.ListOf(main);
        list.TickAllCommand.Execute(null);
        list.ReviewCommand.Execute(null);
        ReviewViewModel? review = main.Review;
        Assert.NotNull(review);
        IReadOnlyList<string> asked = review.CapacityPaths;

        review.BackCommand.Execute(null);
        review.CapacitiesRead(asked.ToDictionary(path => path, _ => (BinCapacity?)ViewModelSamples.Roomy));

        Assert.Equal(Screen.List, main.Screen);
        Assert.Equal(ReviewState.Preparing, review.State);

        list.ReviewCommand.Execute(null);
        review.CapacitiesRead(new Dictionary<string, BinCapacity?> { [@"D:\elsewhere"] = ViewModelSamples.Roomy });

        Assert.Equal(Screen.Review, main.Screen);
        Assert.Equal(ReviewState.Preparing, review.State);
    }

    /// <summary>Each kind of question has its own title and answer labels.</summary>
    /// <param name="kind">The question's kind.</param>
    /// <param name="title">The expected title.</param>
    /// <param name="yes">The expected yes label.</param>
    /// <param name="no">The expected no label.</param>
    [Theory]
    [InlineData(StepKind.Loss, "Would lose work", "Remove anyway", "Keep")]
    [InlineData(StepKind.Link, "Link", "Remove the link", "Keep")]
    [InlineData(StepKind.Permanent, "Permanent delete", "Delete permanently", "Skip it")]
    [InlineData(StepKind.Branch, "Delete branch", "Delete branch after removing", "Keep branch")]
    public void StepTitlesAndLabelsFollowTheKind(StepKind kind, string title, string yes, string no)
    {
        (Candidate candidate, BinCapacity capacity) = kind switch
        {
            StepKind.Loss => ((Candidate)ViewModelSamples.Worktree("a", MergeState.Unmerged(4), 10), ViewModelSamples.Roomy),
            StepKind.Link => (ViewModelSamples.Link("a"), ViewModelSamples.Roomy),
            StepKind.Permanent => (ViewModelSamples.Worktree("a", MergeState.Ancestor, 2 * ViewModelSamples.Mb), ViewModelSamples.Small),
            _ => (ViewModelSamples.Worktree("a", MergeState.Ancestor, 10), ViewModelSamples.Roomy),
        };
        MainViewModel main = ViewModelSamples.Listed(candidate);

        ReviewViewModel review = ViewModelSamples.Reviewing(main, capacity);

        Assert.Equal(ReviewState.Asking, review.State);
        Assert.Equal(title, review.Title);
        Assert.Equal(yes, review.YesLabel);
        Assert.Equal(no, review.NoLabel);
    }

    /// <summary>Back keeps the answers given; entering Review again with the same ticks replays them.</summary>
    [Fact]
    public void BackKeepsAnswersForTheSameTickedSet()
    {
        MainViewModel main = TwoBranches();
        ReviewViewModel review = ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);
        review.Answer(false);
        Assert.Equal(@"repo.wt\b", review.PickPath);

        review.BackCommand.Execute(null);
        Assert.Equal(Screen.List, main.Screen);
        Assert.True(ViewModelSamples.ListOf(main).Rows.All(row => row.IsTicked));
        ViewModelSamples.Reenter(main, ViewModelSamples.Roomy);

        Assert.Equal(ReviewState.Asking, review.State);
        Assert.Equal(@"repo.wt\b", review.PickPath);
    }

    /// <summary>Entering Review with other ticks than the answers were given for asks everything again.</summary>
    [Fact]
    public void ChangedTickedSetClearsAnswers()
    {
        MainViewModel main = TwoBranches();
        ReviewViewModel review = ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);
        review.Answer(false);
        review.BackCommand.Execute(null);

        ViewModelSamples.ListOf(main).Rows[1].IsTicked = false;
        ViewModelSamples.Reenter(main, ViewModelSamples.Roomy);

        Assert.Equal(ReviewState.Asking, review.State);
        Assert.Equal(@"repo.wt\a", review.PickPath);
    }

    /// <summary>Kept answers replay only while each question matches the one answered; the rest are asked again.</summary>
    [Fact]
    public void ReplayStopsAtTheFirstChangedStep()
    {
        MainViewModel main = ViewModelSamples.Listed(
            ViewModelSamples.Worktree("a", MergeState.Ancestor, 2 * ViewModelSamples.Mb),
            ViewModelSamples.Worktree("b", MergeState.Ancestor, 10)
        );
        ReviewViewModel review = ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);
        review.Answer(true);
        review.Answer(true);
        Assert.Equal(ReviewState.Confirming, review.State);
        review.BackCommand.Execute(null);

        ViewModelSamples.Reenter(main, ViewModelSamples.Small);

        Assert.Equal("Permanent delete", review.Title);
        Assert.Equal(@"repo.wt\a", review.PickPath);
        review.Answer(true);
        Assert.Equal("Delete branch", review.Title);
        Assert.Equal(@"repo.wt\a", review.PickPath);
    }

    /// <summary>After the last answer the final sentence is shown, with Cancel the default answer.</summary>
    [Fact]
    public void LastAnswerShowsTheFinalSentenceWithCancelDefault()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
        ReviewViewModel review = ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);

        review.Answer(true);

        Assert.Equal(ReviewState.Confirming, review.State);
        Assert.Equal("Remove 1 item: 1 to the Recycle Bin (10 B); 1 branch deleted.", review.FinalSentence);
        Assert.False(review.DefaultAnswer);
        Assert.Equal(Screen.Review, main.Screen);

        review.ConfirmCommand.Execute(null);

        Assert.Equal(Screen.Removing, main.Screen);
        RemovingViewModel? removing = main.Removing;
        Assert.NotNull(removing);
        Assert.IsType<Plan.Run>(Assert.Single(removing.Decisions).Plan);
    }

    /// <summary>When the answers leave nothing to remove, the removal starts without a confirmation.</summary>
    [Fact]
    public void NothingToRunStartsRemovalAtOnce()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Unmerged(4), 10));
        ReviewViewModel review = ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);

        review.Answer(false);

        Assert.Equal(Screen.Removing, main.Screen);
        RemovingViewModel? removing = main.Removing;
        Assert.NotNull(removing);
        Assert.IsType<Plan.Skip>(Assert.Single(removing.Decisions).Plan);
        Assert.Empty(removing.Rows);
        Assert.Equal("Removing 0/0", removing.Heading);
    }

    /// <summary>Cancel on the final confirmation goes back to the List with the ticks kept.</summary>
    [Fact]
    public void CancelOnConfirmReturnsToTheList()
    {
        MainViewModel main = ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10));
        ReviewViewModel review = ViewModelSamples.Reviewing(main, ViewModelSamples.Roomy);
        review.Answer(true);

        review.CancelCommand.Execute(null);

        Assert.Equal(Screen.List, main.Screen);
        Assert.True(Assert.Single(ViewModelSamples.ListOf(main).Rows).IsTicked);
        Assert.Null(main.Removing);
    }

    /// <summary>Two merged worktrees, <c>a</c> and <c>b</c>, each asked only about its branch.</summary>
    /// <returns>The window, listing them.</returns>
    private static MainViewModel TwoBranches() =>
        ViewModelSamples.Listed(ViewModelSamples.Worktree("a", MergeState.Ancestor, 10), ViewModelSamples.Worktree("b", MergeState.Ancestor, 10));
}
