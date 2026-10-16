using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorktreeSweep.Report;

namespace WorktreeSweep.ViewModels;

/// <summary>
/// The List screen: the candidates to tick, sorted and filtered into the shown rows, the summary under them, the selected row's
/// detail and the way into Review.
/// </summary>
public sealed partial class ListViewModel : ObservableObject
{
    /// <summary>What <see cref="Message"/> says when Review is asked for with no row ticked.</summary>
    public const string NothingPicked = "Nothing picked; tick a row first.";

    /// <summary>The time ages are measured from, in Unix seconds.</summary>
    private readonly long nowUnix;

    /// <summary>The local time zone's offset from UTC at a Unix time.</summary>
    private readonly Func<long, TimeSpan> utcOffset;

    /// <summary>Enters Review with the ticked rows' indexes.</summary>
    private readonly Action<IReadOnlyList<int>> review;

    /// <summary>Whether a command is setting many ticks, so the tick summaries are announced once after the loop.</summary>
    private bool settingTicks;

    /// <summary>Whether the filters are being set together, so the shown rows are rebuilt once rather than once per filter.</summary>
    private bool settingFilters;

    /// <summary>Initializes a new instance of the <see cref="ListViewModel"/> class, showing every row in table order.</summary>
    /// <param name="report">What the scan found: its candidates become the rows and its discovery errors the notice.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <param name="utcOffset">The local time zone's offset from UTC at a Unix time.</param>
    /// <param name="review">Enters Review with the ticked rows' indexes, in table order.</param>
    public ListViewModel(ScanReport report, long nowUnix, Func<long, TimeSpan> utcOffset, Action<IReadOnlyList<int>> review)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(utcOffset);
        ArgumentNullException.ThrowIfNull(review);
        this.nowUnix = nowUnix;
        this.utcOffset = utcOffset;
        this.review = review;
        Rows = [.. ReportTable.Rows(report, nowUnix).Select(row => new CandidateRowViewModel(row))];
        foreach (CandidateRowViewModel row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }
        WorktreeCount = Rows.Count(row => row.RowKind == RowKind.Worktree);
        OrphanCount = Rows.Count(row => row.RowKind == RowKind.Orphan);
        LinkCount = Rows.Count(row => row.RowKind == RowKind.Link);
        NothingLostCount = Rows.Count(row => row.LosesNothing);
        DiscoveryErrors = ReportTable.DiscoveryErrorLines(report);
        Shown = Rows;
        SetFilters(KindFilter.All, nothingLost: false, search: "");
    }

    /// <summary>Gets every row, in table order; never re-ordered, since Review keys its answers on these indexes.</summary>
    public IReadOnlyList<CandidateRowViewModel> Rows { get; }

    /// <summary>Gets the rows the filters let through, in the sort's order; rebuilt on every sort or filter change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMatch))]
    public partial IReadOnlyList<CandidateRowViewModel> Shown { get; private set; }

    /// <summary>Gets the column the shown rows are sorted by; <see langword="null"/> for table order.</summary>
    [ObservableProperty]
    public partial SortKey? SortKey { get; private set; }

    /// <summary>Gets the sort's direction; ascending while <see cref="SortKey"/> is <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial ListSortDirection SortDirection { get; private set; }

    /// <summary>Gets or sets which kind of row is shown.</summary>
    [ObservableProperty]
    public partial KindFilter KindFilter { get; set; }

    /// <summary>Gets or sets a value indicating whether only the rows whose removal loses nothing are shown.</summary>
    [ObservableProperty]
    public partial bool NothingLost { get; set; }

    /// <summary>Gets or sets the text a shown row's path or branch contains, ignoring case; empty to show every row.</summary>
    [ObservableProperty]
    public partial string Search { get; set; }

    /// <summary>Gets how many rows there are, for the All button.</summary>
    public int AllCount => Rows.Count;

    /// <summary>Gets how many rows are registered worktrees, for the Worktrees button.</summary>
    public int WorktreeCount { get; }

    /// <summary>Gets how many rows are orphan folders, for the Orphans button.</summary>
    public int OrphanCount { get; }

    /// <summary>Gets how many rows are orphan links, for the Links button.</summary>
    public int LinkCount { get; }

    /// <summary>Gets how many rows lose nothing when removed, for the Nothing lost button.</summary>
    public int NothingLostCount { get; }

    /// <summary>Gets a value indicating whether the filters hide every row.</summary>
    public bool NoMatch => Shown.Count == 0;

    /// <summary>Gets the notice's title, <c>{n} discovery error(s).</c>; <see langword="null"/> when the scan had none.</summary>
    public string? DiscoveryErrorsTitle =>
        DiscoveryErrors.Count switch
        {
            0 => null,
            1 => "1 discovery error.",
            int count => string.Create(CultureInfo.InvariantCulture, $"{count} discovery errors."),
        };

    /// <summary>Gets the scan's discovery errors as <c>{path}: {message}</c>, the path relative to the root and each on one line.</summary>
    public IReadOnlyList<string> DiscoveryErrors { get; }

    /// <summary>Gets what the notice says the discovery errors mean for the orphans.</summary>
    public string DiscoveryErrorsNote =>
        DiscoveryErrors.Count == 1
            ? "Orphans inside its worktree folders are planned as skips, since git may still use them."
            : "Orphans inside their worktree folders are planned as skips, since git may still use them.";

    /// <summary>Gets or sets the highlighted row; <see langword="null"/> when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(SelectedLosesNothing))]
    public partial CandidateRowViewModel? Selected { get; set; }

    /// <summary>Gets the selected row's labelled detail fields; empty when no row is selected.</summary>
    public IReadOnlyList<DetailField> Detail => Selected is { } selected ? DetailText.Fields(selected.Candidate, nowUnix, utcOffset) : [];

    /// <summary>Gets a value indicating whether a row is selected and removing it loses nothing.</summary>
    public bool SelectedLosesNothing => Selected?.LosesNothing ?? false;

    /// <summary>
    /// Gets the summary: <c>{N} candidates · {shown} shown · {K} ticked · {size} selected for removal</c>, the size reading
    /// <c>at least</c> when any ticked size is unknown or only partly read.
    /// </summary>
    public string Header
    {
        get
        {
            KnownSize?[] sizes = [.. Rows.Where(row => row.IsTicked).Select(row => row.Candidate.KnownSize)];
            long bytes = sizes.Sum(size => size?.Bytes ?? 0);
            bool atLeast = sizes.Any(size => size is null or { Partial: true });
            string selected = ReportTable.SizeText(bytes, atLeast);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Rows.Count} candidates · {Shown.Count} shown · {sizes.Length} ticked · {selected} selected for removal"
            );
        }
    }

    /// <summary>Gets the Review button's text: <c>Review</c>, or <c>Review {K} pick(s)…</c> once a row is ticked.</summary>
    public string ReviewLabel =>
        TickedCount switch
        {
            0 => "Review",
            1 => "Review 1 pick…",
            int count => string.Create(CultureInfo.InvariantCulture, $"Review {count} picks…"),
        };

    /// <summary>Gets how many ticked rows the filters hide.</summary>
    public int HiddenTicked => TickedCount - Shown.Count(row => row.IsTicked);

    /// <summary>Gets the sentence that says ticked rows are hidden; <see langword="null"/> when none is.</summary>
    public string? HiddenTickedText =>
        HiddenTicked switch
        {
            0 => null,
            1 => "1 ticked row is hidden by the filter.",
            int count => string.Create(CultureInfo.InvariantCulture, $"{count} ticked rows are hidden by the filter."),
        };

    /// <summary>
    /// Gets or sets a value indicating whether there are shown rows and every one is ticked, for the header checkbox; setting it ticks
    /// or unticks the shown rows only.
    /// </summary>
    public bool AllShownTicked
    {
        get => Shown.Count > 0 && Shown.All(row => row.IsTicked);
        set => SetTicks(Shown, value);
    }

    /// <summary>Gets the message shown under the list; <see langword="null"/> when there is none.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Gets how many rows are ticked, shown or not.</summary>
    private int TickedCount => Rows.Count(row => row.IsTicked);

    /// <summary>Ticks the shown rows.</summary>
    [RelayCommand]
    private void TickShown() => SetTicks(Shown, true);

    /// <summary>Unticks every row, the hidden ones too.</summary>
    [RelayCommand]
    private void UntickAll() => SetTicks(Rows, false);

    /// <summary>Shows every row again: every kind, whatever it loses, and no search.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        SetFilters(KindFilter.All, nothingLost: false, search: "");
        Rebuild();
    }

    /// <summary>Sets the three filters without rebuilding the shown rows for each; the caller rebuilds once if it needs to.</summary>
    /// <param name="kind">The kind filter.</param>
    /// <param name="nothingLost">Whether only rows that lose nothing are shown.</param>
    /// <param name="search">The search.</param>
    private void SetFilters(KindFilter kind, bool nothingLost, string search)
    {
        settingFilters = true;
        try
        {
            KindFilter = kind;
            NothingLost = nothingLost;
            Search = search;
        }
        finally
        {
            settingFilters = false;
        }
    }

    /// <summary>Sorts by <paramref name="key"/>: a new column starts ascending, then descending, then back to table order.</summary>
    /// <param name="key">The column.</param>
    [RelayCommand]
    private void SortBy(SortKey key)
    {
        if (SortKey != key)
        {
            SortKey = key;
            SortDirection = ListSortDirection.Ascending;
        }
        else if (SortDirection == ListSortDirection.Ascending)
        {
            SortDirection = ListSortDirection.Descending;
        }
        else
        {
            SortKey = null;
            SortDirection = ListSortDirection.Ascending;
        }
        Rebuild();
    }

    /// <summary>Enters Review with every ticked row, hidden ones included, in table order; with none ticked, says so and stays.</summary>
    [RelayCommand]
    private void Review()
    {
        int[] ticked = [.. Enumerable.Range(0, Rows.Count).Where(index => Rows[index].IsTicked)];
        if (ticked.Length == 0)
        {
            Message = NothingPicked;
            return;
        }
        Message = null;
        review(ticked);
    }

    /// <summary>Rebuilds the shown rows when the kind filter changes.</summary>
    /// <param name="value">The new filter.</param>
    partial void OnKindFilterChanged(KindFilter value) => RebuildUnlessSettingFilters();

    /// <summary>Rebuilds the shown rows when Nothing lost changes.</summary>
    /// <param name="value">The new value.</param>
    partial void OnNothingLostChanged(bool value) => RebuildUnlessSettingFilters();

    /// <summary>Rebuilds the shown rows when the search changes.</summary>
    /// <param name="value">The new search.</param>
    partial void OnSearchChanged(string value) => RebuildUnlessSettingFilters();

    /// <summary>Rebuilds the shown rows after one filter changed, unless <see cref="SetFilters"/> is setting all three.</summary>
    private void RebuildUnlessSettingFilters()
    {
        if (!settingFilters)
        {
            Rebuild();
        }
    }

    /// <summary>
    /// Filters and sorts the rows into <see cref="Shown"/>, keeps the selection while its row is shown and clears it otherwise, and
    /// announces the summaries that count the shown rows.
    /// </summary>
    private void Rebuild()
    {
        CandidateRowViewModel? selected = Selected;
        Shown = [.. RowOrder.Sort(Rows.Where(IsShown), SortKey, SortDirection)];
        Selected = selected is not null && Shown.Contains(selected) ? selected : null;
        AnnounceTicks();
    }

    /// <summary>Whether <paramref name="row"/> passes the kind filter, Nothing lost and the search.</summary>
    /// <param name="row">The row.</param>
    /// <returns><see langword="true"/> when the row is shown.</returns>
    private bool IsShown(CandidateRowViewModel row) => IsOfShownKind(row) && (!NothingLost || row.LosesNothing) && MatchesSearch(row);

    /// <summary>Whether <paramref name="row"/> is of the kind the filter shows.</summary>
    /// <param name="row">The row.</param>
    /// <returns><see langword="true"/> when the kind is shown.</returns>
    private bool IsOfShownKind(CandidateRowViewModel row) =>
        KindFilter switch
        {
            KindFilter.All => true,
            KindFilter.Worktree => row.RowKind == RowKind.Worktree,
            KindFilter.Orphan => row.RowKind == RowKind.Orphan,
            KindFilter.Link => row.RowKind == RowKind.Link,
            _ => throw new InvalidOperationException($"unknown kind filter {KindFilter}"),
        };

    /// <summary>Whether <paramref name="row"/>'s path or full branch contains the search, ignoring case.</summary>
    /// <param name="row">The row.</param>
    /// <returns><see langword="true"/> when the search is empty or matches.</returns>
    private bool MatchesSearch(CandidateRowViewModel row) =>
        Search.Length == 0
        || row.Path.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || row.FullBranch.Contains(Search, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets the tick of each of <paramref name="rows"/>, clears the message and announces the tick summaries once.</summary>
    /// <param name="rows">The rows to set.</param>
    /// <param name="ticked">The tick to set.</param>
    private void SetTicks(IEnumerable<CandidateRowViewModel> rows, bool ticked)
    {
        settingTicks = true;
        try
        {
            foreach (CandidateRowViewModel row in rows)
            {
                row.IsTicked = ticked;
            }
        }
        finally
        {
            settingTicks = false;
        }
        Message = null;
        AnnounceTicks();
    }

    /// <summary>Clears the message and announces the tick summaries when a row's tick changes.</summary>
    /// <param name="sender">The row.</param>
    /// <param name="e">Which property changed.</param>
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CandidateRowViewModel.IsTicked))
        {
            return;
        }
        Message = null;
        if (!settingTicks)
        {
            AnnounceTicks();
        }
    }

    /// <summary>Announces every property that counts the ticked or the shown rows.</summary>
    private void AnnounceTicks()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(ReviewLabel));
        OnPropertyChanged(nameof(HiddenTicked));
        OnPropertyChanged(nameof(HiddenTickedText));
        OnPropertyChanged(nameof(AllShownTicked));
    }
}
