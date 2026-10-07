using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorktreeSweep.Report;

namespace WorktreeSweep.ViewModels;

/// <summary>The List screen: the candidates to tick, the header that sums them up, the selected row's detail and the way into Review.</summary>
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

    /// <summary>Whether Tick all or Tick none is setting every row, so the header is announced once after the loop.</summary>
    private bool settingTicks;

    /// <summary>Initializes a new instance of the <see cref="ListViewModel"/> class.</summary>
    /// <param name="rows">The table rows, in table order.</param>
    /// <param name="nowUnix">The time ages are measured from, in Unix seconds.</param>
    /// <param name="utcOffset">The local time zone's offset from UTC at a Unix time.</param>
    /// <param name="review">Enters Review with the ticked rows' indexes, in table order.</param>
    public ListViewModel(IReadOnlyList<ReportRow> rows, long nowUnix, Func<long, TimeSpan> utcOffset, Action<IReadOnlyList<int>> review)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(utcOffset);
        ArgumentNullException.ThrowIfNull(review);
        this.nowUnix = nowUnix;
        this.utcOffset = utcOffset;
        this.review = review;
        Rows = [.. rows.Select(row => new CandidateRowViewModel(row))];
        foreach (CandidateRowViewModel row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    /// <summary>Gets the rows, in table order.</summary>
    public IReadOnlyList<CandidateRowViewModel> Rows { get; }

    /// <summary>Gets or sets the highlighted row; <see langword="null"/> when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial CandidateRowViewModel? Selected { get; set; }

    /// <summary>Gets the selected row's seven detail lines; empty when no row is selected.</summary>
    public IReadOnlyList<string> Detail => Selected is { } selected ? DetailText.Lines(selected.Candidate, nowUnix, utcOffset) : [];

    /// <summary>
    /// Gets the header: <c>{N} candidates, {K} ticked, {size} selected for removal</c>, the size reading <c>at least</c> when any
    /// ticked size is unknown or only partly read.
    /// </summary>
    public string Header
    {
        get
        {
            KnownSize?[] sizes = [.. Rows.Where(row => row.IsTicked).Select(row => row.Candidate.KnownSize)];
            long bytes = sizes.Sum(size => size?.Bytes ?? 0);
            bool atLeast = sizes.Any(size => size is null or { Partial: true });
            string selected = ReportTable.SizeText(bytes, atLeast);
            return string.Create(CultureInfo.InvariantCulture, $"{Rows.Count} candidates, {sizes.Length} ticked, {selected} selected for removal");
        }
    }

    /// <summary>Gets the message shown under the list; <see langword="null"/> when there is none.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Ticks every row.</summary>
    [RelayCommand]
    private void TickAll() => SetTicks(true);

    /// <summary>Unticks every row.</summary>
    [RelayCommand]
    private void TickNone() => SetTicks(false);

    /// <summary>Enters Review with the ticked rows; with none ticked, says so and stays on the List.</summary>
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

    /// <summary>Sets every row's tick, clears the message and announces the header once.</summary>
    /// <param name="ticked">The tick to set.</param>
    private void SetTicks(bool ticked)
    {
        settingTicks = true;
        try
        {
            foreach (CandidateRowViewModel row in Rows)
            {
                row.IsTicked = ticked;
            }
        }
        finally
        {
            settingTicks = false;
        }
        Message = null;
        OnPropertyChanged(nameof(Header));
    }

    /// <summary>Clears the message and refreshes the header when a row's tick changes.</summary>
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
            OnPropertyChanged(nameof(Header));
        }
    }
}
