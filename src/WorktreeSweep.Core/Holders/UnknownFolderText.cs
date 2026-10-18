namespace WorktreeSweep.Holders;

/// <summary>The wording for a folder whose users the file system could not list, so what holds it is not known.</summary>
internal static class UnknownFolderText
{
    /// <summary>The line naming <paramref name="path"/> as one whose holders cannot be known.</summary>
    /// <param name="path">The folder.</param>
    /// <returns>The line.</returns>
    public static string Line(string path) => $"{path} cannot be read here, so it is not known what holds it.";
}
