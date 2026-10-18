namespace WorktreeSweep;

/// <summary>One completed scan of a folder, as the Choose screen lists it.</summary>
/// <param name="Folder">The full path of the folder scanned.</param>
/// <param name="ScannedUnix">When the scan completed, in Unix seconds (UTC).</param>
/// <param name="Candidates">How many candidates the scan found.</param>
/// <param name="Bytes">The candidates' total known size.</param>
public sealed record RecentFolder(string Folder, long ScannedUnix, int Candidates, long Bytes);
