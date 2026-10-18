namespace WorktreeSweep.ViewModels;

/// <summary>A candidate being read now, on the Scanning screen's working list.</summary>
/// <param name="Path">Its path, relative to the scanned root.</param>
/// <param name="Action">What its read is doing: <c>git status</c>, <c>measuring size · 41,207 files so far</c>.</param>
public sealed record WorkingRow(string Path, string Action);
