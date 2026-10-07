namespace WorktreeSweep.Review;

/// <summary>What removing a pick loses, without the pick's path.</summary>
/// <param name="IsLink">
/// Whether the pick is a link, of which only the link goes; otherwise removing it loses work or leaves a registration behind.
/// </param>
/// <param name="Text">
/// The loss as a sentence with a capital first letter that names no path of the pick (<c>Remove the link only; X:\dev\yaat is not
/// touched.</c>).
/// </param>
/// <param name="Question">The short question, naming no path.</param>
public sealed record Loss(bool IsLink, string Text, string Question);
