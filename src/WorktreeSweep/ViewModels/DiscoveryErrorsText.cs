using System.Globalization;

namespace WorktreeSweep.ViewModels;

/// <summary>The discovery-error notice's wording, shared by the List and Empty screens.</summary>
internal static class DiscoveryErrorsText
{
    /// <summary>
    /// The notice's title for <paramref name="count"/> discovery errors, <c>{n} discovery error(s).</c>; <see langword="null"/> when
    /// there are none.
    /// </summary>
    /// <param name="count">How many discovery errors there are.</param>
    /// <returns>The title, or <see langword="null"/> when <paramref name="count"/> is zero.</returns>
    internal static string? Title(int count) =>
        count switch
        {
            0 => null,
            1 => "1 discovery error.",
            _ => string.Create(CultureInfo.InvariantCulture, $"{count} discovery errors."),
        };
}
