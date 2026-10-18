namespace WorktreeSweep.ViewModels;

/// <summary>A recent folder on the Choose screen.</summary>
/// <param name="Folder">Its full path.</param>
/// <param name="Summary">When it was scanned and what was found: <c>yesterday · 14 candidates · 9.6 GB</c>.</param>
public sealed record RecentRow(string Folder, string Summary);
