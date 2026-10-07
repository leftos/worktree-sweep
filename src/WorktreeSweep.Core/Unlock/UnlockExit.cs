namespace WorktreeSweep.Unlock;

/// <summary>The exit codes the elevated unlock session ends with. 1 is an error and 2 a usage error, which the command line
/// uses for itself.</summary>
public static class UnlockExit
{
    /// <summary>Nothing holds files under the paths any more.</summary>
    public const int AllClear = 0;

    /// <summary>Some processes still hold files, after at least one was stopped or had its handles closed.</summary>
    public const int SomeLeft = 3;

    /// <summary>Processes still hold files and nothing was acted on.</summary>
    public const int NothingDone = 4;

    /// <summary>The session's exit code.</summary>
    /// <param name="clear">Whether nothing holds files under the paths.</param>
    /// <param name="acted">Whether anything was stopped or had its handles closed.</param>
    /// <returns><see cref="AllClear"/> when clear, <see cref="SomeLeft"/> when something was acted on, otherwise
    /// <see cref="NothingDone"/>.</returns>
    public static int For(bool clear, bool acted) =>
        clear ? AllClear
        : acted ? SomeLeft
        : NothingDone;
}
