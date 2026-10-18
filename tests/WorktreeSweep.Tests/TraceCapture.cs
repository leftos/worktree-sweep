using System.Diagnostics;
using System.Text;

namespace WorktreeSweep.Tests;

/// <summary>
/// Collects what is traced while it is alive, in the order it was traced. A test class that uses it must be in
/// <see cref="ProcessEnvironment.Name"/>, because <see cref="Trace.Listeners"/> is process-wide.
/// </summary>
internal sealed class TraceCapture : TraceListener
{
    private readonly StringBuilder text = new();

    private readonly Lock gate = new();

    /// <summary>Initializes a new instance of the <see cref="TraceCapture"/> class and starts collecting.</summary>
    public TraceCapture() => Trace.Listeners.Add(this);

    /// <summary>Everything traced while this capture was alive.</summary>
    public string Text
    {
        get
        {
            lock (gate)
            {
                return text.ToString();
            }
        }
    }

    /// <inheritdoc/>
    public override void Write(string? message)
    {
        lock (gate)
        {
            text.Append(message);
        }
    }

    /// <inheritdoc/>
    public override void WriteLine(string? message)
    {
        lock (gate)
        {
            text.AppendLine(message);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        Trace.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}
