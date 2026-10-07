using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using WorktreeSweep.Git;
using WorktreeSweep.Report;
using WorktreeSweep.Scan;

namespace WorktreeSweep;

/// <summary>The command line: <c>worktree-sweep [ROOT] [--list | --json]</c>.</summary>
internal static class Program
{
    private const int Success = 0;
    private const int Failure = 1;
    private const int UsageError = 2;

    /// <summary>The environment variable that, set to <c>debug</c>, shows debug traces on standard error.</summary>
    private const string LogVariable = "WORKTREE_SWEEP_LOG";

    private static int Main(string[] args)
    {
        Console.SetOut(Utf8Writer(Console.OpenStandardOutput()));
        Console.SetError(Utf8Writer(Console.OpenStandardError()));
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
            Console.Error.WriteLine("worktree-sweep: the window is not built yet; use --list or --json");
            return UsageError;
        }
        try
        {
            ScanReport report = Scanner.Scan(root);
            if (json)
            {
                using Stream stdout = Console.OpenStandardOutput();
                ReportJson.Write(report, stdout);
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

    /// <summary>A writer over a standard stream that writes UTF-8 without a BOM and <c>\n</c> line ends, flushing every write.</summary>
    private static StreamWriter Utf8Writer(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };

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
