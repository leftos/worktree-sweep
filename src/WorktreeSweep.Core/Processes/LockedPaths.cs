namespace WorktreeSweep.Processes;

/// <summary>Which handle names fall under the folders a removal found locked.</summary>
public static class LockedPaths
{
    private const string VerbatimUnc = @"\\?\UNC\";
    private const string NtUnc = @"\??\UNC\";
    private const string Verbatim = @"\\?\";
    private const string Nt = @"\??\";

    /// <summary>
    /// Whether a handle's object <paramref name="name"/> is one of <paramref name="paths"/> or lies under one. Paths compare
    /// case-insensitively with <c>\</c> separators, without a <c>\\?\</c> or <c>\??\</c> prefix (<c>\\?\UNC\</c> reads as
    /// <c>\\</c>) or a trailing <c>\</c>; <c>D:\a\x</c> does not cover <c>D:\a\xy</c>. A locked path left empty by that, such
    /// as <c>\</c> or <c>\\?\</c>, matches nothing.
    /// </summary>
    /// <param name="name">The handle's object name.</param>
    /// <param name="paths">The locked paths.</param>
    /// <returns><see langword="true"/> when the name is a locked path or under one.</returns>
    public static bool Matches(string name, IReadOnlyList<string> paths)
    {
        string comparableName = Comparable(name);
        return paths.Any(path =>
        {
            string comparablePath = Comparable(path);
            if (comparablePath.Length == 0)
            {
                return false;
            }
            return comparableName == comparablePath
                || (
                    comparableName.StartsWith(comparablePath, StringComparison.Ordinal)
                    && comparableName.Length > comparablePath.Length
                    && comparableName[comparablePath.Length] == '\\'
                );
        });
    }

    private static string Comparable(string path)
    {
        string text = path.Replace('/', '\\');
        string? unc = StripPrefix(text, VerbatimUnc) ?? StripPrefix(text, NtUnc);
        text = unc is not null ? @"\\" + unc : StripPrefix(text, Verbatim) ?? StripPrefix(text, Nt) ?? text;
        return text.TrimEnd('\\').ToLowerInvariant();
    }

    private static string? StripPrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : null;
}
