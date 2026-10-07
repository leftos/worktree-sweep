using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace WorktreeSweep;

/// <summary>
/// Shows help as System.CommandLine does, leaving the scan's ROOT argument out of a subcommand's help: the help copies every
/// parent command's arguments into a subcommand's usage line and Arguments section, and <c>remove</c> and <c>unlock</c> take no ROOT.
/// </summary>
/// <param name="inner">The help action the root command's <c>--help</c> option carries.</param>
/// <param name="root">The scan's ROOT argument, hidden only while a subcommand's help is written.</param>
internal sealed class SubcommandHelpAction(HelpAction inner, Argument root) : SynchronousCommandLineAction
{
    /// <summary>Whether the parse errors are cleared, as the wrapped help action does, so <c>subcommand --help</c> never fails.</summary>
    public override bool ClearsParseErrors => true;

    /// <summary>Hides ROOT unless the help is the root command's own, writes the help, and shows ROOT again after.</summary>
    /// <param name="parseResult">The parsed command line the help is written for.</param>
    /// <returns>The help action's exit code.</returns>
    public override int Invoke(ParseResult parseResult)
    {
        bool wasHidden = root.Hidden;
        root.Hidden = wasHidden || parseResult.CommandResult.Command is not RootCommand;
        try
        {
            return inner.Invoke(parseResult);
        }
        finally
        {
            root.Hidden = wasHidden;
        }
    }
}
