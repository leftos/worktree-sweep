using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WorktreeSweep.Discovery;

/// <summary>
/// Maps a loopback admin-share spelling of a path (<c>\\localhost\X$\dev\x</c>, as git records a worktree added through one) to the
/// drive path it names, so a scan rooted at the drive matches a worktree registered through the share.
/// </summary>
/// <remarks>
/// An admin share names a whole volume, so the mapping is by spelling: no handle-based one exists, because
/// <see cref="PathResolver.FinalPath"/> keeps the <c>\\?\UNC\{host}\...</c> spelling of a loopback admin share. Only this machine's
/// own hosts name a volume of this machine, so a share of any other host is left alone, and so are a share of a folder, an IPv6
/// literal and a path that is not a share at all.
/// </remarks>
internal static class LoopbackShare
{
    /// <summary>The two leading separators every share spelling starts with.</summary>
    private const string SharePrefix = @"\\";

    /// <summary>The prefix Windows puts on the resolved spelling of a share.</summary>
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>Every admin share name is a letter followed by this character.</summary>
    private const char ShareSuffix = '$';

    /// <summary>The separator both spellings of a share use.</summary>
    private const char Separator = '\\';

    /// <summary>This machine's names, read once.</summary>
    private static readonly Lazy<HashSet<string>> Hosts = new(ReadLocalHosts);

    /// <summary>The hosts whose admin shares name a volume of this machine: the loopback names and this machine's own names.</summary>
    /// <returns>A case-insensitive set; a name that cannot be read is traced and left out.</returns>
    internal static IReadOnlySet<string> LocalHosts() => Hosts.Value;

    /// <summary>The drive path <paramref name="path"/> spells as a loopback admin share, or <see langword="null"/> when it is not one.</summary>
    /// <remarks><c>/</c> and <c>\</c> both separate, as git records the path. The letter is returned upper-case.</remarks>
    /// <param name="path">A path.</param>
    /// <param name="localHosts">The hosts whose admin shares name a volume of this machine.</param>
    /// <returns>The drive path, or <see langword="null"/>.</returns>
    internal static string? ToLocalDrive(string path, IReadOnlySet<string> localHosts)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(localHosts);
        string? afterPrefix = AfterPrefix(path.Replace('/', Separator));
        if (afterPrefix is null)
        {
            return null;
        }
        int separator = afterPrefix.IndexOf(Separator);
        if (separator <= 0)
        {
            return null;
        }
        string host = afterPrefix[..separator];
        return IsAdminShare(afterPrefix[(separator + 1)..], out string root, out string rest) && localHosts.Contains(host) ? root + rest : null;
    }

    /// <summary>
    /// <paramref name="path"/> after a <c>\\?\UNC\</c> prefix or its two leading separators, or <see langword="null"/> when it is
    /// neither.
    /// </summary>
    /// <param name="path">A path with <c>\</c> separators.</param>
    /// <returns>The rest of the path, or <see langword="null"/>.</returns>
    private static string? AfterPrefix(string path) =>
        path.StartsWith(UncPrefix, StringComparison.Ordinal) ? path[UncPrefix.Length..]
        : path.StartsWith(SharePrefix, StringComparison.Ordinal) ? path[SharePrefix.Length..]
        : null;

    /// <summary>The drive root and the tail of an admin share name such as <c>X$</c> or <c>X$\dev\x</c>.</summary>
    /// <param name="share">The path part after the host.</param>
    /// <param name="root">The drive root, as <c>X:</c>.</param>
    /// <param name="rest">The tail after the share: a separator alone when the share names a volume root, else <c>\rest</c>.</param>
    /// <returns><see langword="true"/> when <paramref name="share"/> is exactly one ASCII letter and <c>$</c>, then a separator or the end.</returns>
    private static bool IsAdminShare(string share, out string root, out string rest)
    {
        root = "";
        rest = "";
        if (share.Length < 2 || share[1] != ShareSuffix || !IsAsciiLetter(share[0]))
        {
            return false;
        }
        if (share.Length > 2 && share[2] != Separator)
        {
            return false;
        }
        root = share[..1].ToUpperInvariant() + ":";
        rest = share.Length > 2 ? share[2..] : @"\";
        return true;
    }

    /// <summary>Whether <paramref name="value"/> is an ASCII letter, which every volume letter is.</summary>
    /// <param name="value">A character.</param>
    /// <returns><see langword="true"/> for <c>a</c>-<c>z</c> and <c>A</c>-<c>Z</c>.</returns>
    private static bool IsAsciiLetter(char value) => (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');

    /// <summary>
    /// The loopback names and this machine's own names; a name that cannot be read is traced and left out, so building the set never
    /// throws and the loopback names are always in it.
    /// </summary>
    /// <returns>A case-insensitive set.</returns>
    private static HashSet<string> ReadLocalHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1" };
        try
        {
            AddHost(hosts, Environment.MachineName);
        }
        catch (InvalidOperationException error)
        {
            Trace.TraceWarning($"cannot read this machine's name: {error.Message}");
        }
        try
        {
            AddHost(hosts, Dns.GetHostName());
        }
        catch (SocketException error)
        {
            Trace.TraceWarning($"cannot read this machine's DNS name: {error.Message}");
        }
        return hosts;
    }

    /// <summary>Adds <paramref name="name"/> to <paramref name="hosts"/>, unless it is empty.</summary>
    /// <param name="hosts">The host names.</param>
    /// <param name="name">A host name.</param>
    private static void AddHost(HashSet<string> hosts, string name)
    {
        if (name.Length > 0)
        {
            _ = hosts.Add(name);
        }
    }
}
