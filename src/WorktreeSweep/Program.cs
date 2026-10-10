using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using WorktreeSweep.Agent;
using WorktreeSweep.Git;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;
using WorktreeSweep.Unlock;

namespace WorktreeSweep;

/// <summary>
/// The command line: <c>worktree-sweep [ROOT] [--list | --json]</c>, and <c>worktree-sweep remove PATH --json [--force]
/// [--stop-build-servers]</c>, which removes one worktree for an agent.
/// </summary>
internal static class Program
{
    private const int Success = 0;

    /// <summary>The exit code of a run that failed.</summary>
    internal const int Failure = 1;
    private const int UsageError = 2;

    /// <summary>The environment variable that, set to <c>debug</c>, shows debug traces on standard error.</summary>
    private const string LogVariable = "WORKTREE_SWEEP_LOG";

    [STAThread]
    private static int Main(string[] args)
    {
        ConsoleEncodings.InstallStandardWriters();
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new StderrTraceListener(showDebug: Environment.GetEnvironmentVariable(LogVariable) == "debug"));
        Trace.AutoFlush = true;

        Argument<string> root = new("ROOT")
        {
            Description = "Folder to scan (default: the current directory).",
            DefaultValueFactory = _ => Environment.CurrentDirectory,
        };
        Option<bool> json = new("--json") { Description = "Print the report as one JSON document and exit." };
        Option<bool> list = new("--list") { Description = "List the candidates and exit." };
        RootCommand command = new("Find stale git worktrees and orphan worktree folders under ROOT.") { root, json, list };
        command.Validators.Add(result =>
        {
            if (result.GetValue(json) && result.GetValue(list))
            {
                result.AddError("--json and --list cannot be used together.");
            }
        });
        command.SetAction(parsed => Run(parsed.GetRequiredValue(root), parsed.GetValue(json), parsed.GetValue(list)));
        command.Subcommands.Add(UnlockCommand());
        command.Subcommands.Add(RemoveCommand());
        // The help copies the root's ROOT argument into a subcommand's usage and Arguments section, so hide it while one runs.
        HelpOption help = command.Options.OfType<HelpOption>().Single();
        help.Action = new SubcommandHelpAction((HelpAction)help.Action!, root);

        ParseResult parsed = command.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (ParseError error in parsed.Errors)
            {
                Console.Error.WriteLine($"error: {error.Message}");
            }
            return UsageError;
        }
        return parsed.Invoke();
    }

    private static int Run(string root, bool json, bool list)
    {
        if (!json && !list)
        {
            return WindowMode.Run(root);
        }
        try
        {
            ScanReport report = Scanner.Scan(root);
            if (json)
            {
                ReportJson.Write(report, Console.Out);
            }
            else
            {
                Console.Out.Write(ReportTable.Render(report, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            }
            return Success;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or GitException)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return Failure;
        }
    }

    /// <summary>
    /// The hidden <c>unlock PATHS... [--caller-pid PID] [--caller-started TIME] [--sweep-pid PID]</c> subcommand that <c>sudo</c>
    /// runs elevated: finds what holds files under the paths and offers to stop it or close its handles.
    /// </summary>
    private static Command UnlockCommand()
    {
        Argument<string[]> paths = new("PATHS") { Arity = ArgumentArity.OneOrMore, Description = "The locked folders." };
        Option<int?> callerPid = new("--caller-pid") { Hidden = true, Description = "The process that started the unelevated run." };
        Option<ulong?> callerStarted = new("--caller-started")
        {
            Hidden = true,
            Description = "That process's creation time, proving its PID still names it.",
        };
        Option<int?> sweepPid = new("--sweep-pid") { Hidden = true, Description = "The unelevated worktree-sweep, never offered." };
        Command unlock = new("unlock", "Find and clear what holds files under PATHS (run elevated by the sweep).")
        {
            paths,
            callerPid,
            callerStarted,
            sweepPid,
        };
        unlock.Hidden = true;
        unlock.SetAction(parsed =>
            RunUnlock(parsed.GetRequiredValue(paths), parsed.GetValue(callerPid), parsed.GetValue(callerStarted), parsed.GetValue(sweepPid))
        );
        return unlock;
    }

    private static int RunUnlock(string[] paths, int? callerPid, ulong? callerStarted, int? sweepPid)
    {
        try
        {
            CodePageReach.RegisterProvider();
            IReadOnlyList<Encoding> codePages = CodePageReach.SystemCodePages();
            var session = new ElevatedSession(
                Console.In,
                Console.Out,
                new HandleExe(),
                new ProcessControl(),
                path => CodePageReach.CanName(path, codePages)
            );
            return session.Run(paths, callerPid, callerStarted, sweepPid);
        }
#pragma warning disable CA1031 // Every failure of the elevated session ends it with exit 1 and its message.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return Failure;
        }
    }

    /// <summary>
    /// The <c>remove PATH --json [--force] [--stop-build-servers]</c> subcommand agents call: removes one worktree without asking,
    /// or reports what holds it and marks it released, as one JSON document whose status the exit code carries.
    /// </summary>
    private static Command RemoveCommand()
    {
        Argument<string> path = new("PATH") { Description = "The root folder of a registered linked worktree." };
        Option<bool> json = new("--json") { Arity = ArgumentArity.Zero, Description = "Print the result as one JSON document (required)." };
        Option<bool> force = new("--force")
        {
            Description = "Remove it even when uncommitted, unmerged or unpushed work would be lost; lifts a git lock first.",
        };
        Option<bool> stopBuildServers = new("--stop-build-servers")
        {
            Description = "Stop the build and language servers holding it (cargo, rust-analyzer, .NET build servers), then retry.",
        };
        Command remove = new("remove", "Remove one worktree without asking, or report what holds it and mark it released (for agents).")
        {
            path,
            json,
            force,
            stopBuildServers,
        };
        // The option's Required = true lets a missing --json through, so the validator checks that the command line gave it.
        remove.Validators.Add(result =>
        {
            if (result.GetResult(json) is not { Implicit: false })
            {
                result.AddError("Option '--json' is required.");
            }
        });
        remove.SetAction(parsed => RunRemove(parsed.GetRequiredValue(path), parsed.GetValue(force), parsed.GetValue(stopBuildServers)));
        return remove;
    }

    /// <summary>
    /// Runs the removal and writes its report once it is complete, so a failure leaves standard output empty and ends with exit 1.
    /// </summary>
    private static int RunRemove(string path, bool force, bool stopBuildServers)
    {
        try
        {
            RemoveReport report = AgentRemover.Run(path, new AgentOptions(force, stopBuildServers));
            RemoveReportJson.Write(report, Console.Out);
            return RemoveExitCode.For(report.Status);
        }
#pragma warning disable CA1031 // Every failure of an agent's removal ends with exit 1 and its message on stderr, as the agent contract says.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return Failure;
        }
    }

    /// <summary>
    /// Writes trace warnings and errors to standard error as <c>warning: </c> and <c>error: </c> lines, and debug traces as
    /// <c>debug: </c> lines when asked to; each line of a message gets the prefix, so every stderr line is one diagnostic.
    /// </summary>
    private sealed class StderrTraceListener(bool showDebug) : TraceListener
    {
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            string label = eventType switch
            {
                TraceEventType.Critical or TraceEventType.Error => "error",
                TraceEventType.Warning => "warning",
                _ => "debug",
            };
            Emit(label, message);
        }

        public override void TraceEvent(
            TraceEventCache? eventCache,
            string source,
            TraceEventType eventType,
            int id,
            string? format,
            params object?[]? args
        ) => TraceEvent(eventCache, source, eventType, id, args is null ? format : string.Format(CultureInfo.InvariantCulture, format ?? "", args));

        public override void Write(string? message) => Emit("debug", message);

        public override void WriteLine(string? message) => Emit("debug", message);

        private void Emit(string label, string? message)
        {
            if (label == "debug" && !showDebug)
            {
                return;
            }
            foreach (string line in (message ?? "").ReplaceLineEndings("\n").Split('\n'))
            {
                Console.Error.WriteLine($"{label}: {line}");
            }
        }
    }
}
