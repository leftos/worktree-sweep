namespace WorktreeSweep.Unlock;

/// <summary>How <c>sudo</c> is configured, as <c>sudo config</c> reports it.</summary>
public enum SudoMode
{
    /// <summary>The elevated process shares the console, input included. The only mode the unlock flow can use.</summary>
    Inline,

    /// <summary>The elevated process runs in a new console window, and sudo may return before it ends.</summary>
    ForceNewWindow,

    /// <summary>The elevated process shares the console but gets no input.</summary>
    DisableInput,

    /// <summary>sudo is turned off.</summary>
    Disabled,

    /// <summary>Output this program does not recognise.</summary>
    Unknown,
}

/// <summary>The mode's name as a sentence shows it.</summary>
public static class SudoModeExtensions
{
    /// <summary>The mode's name as a sentence shows it.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns><c>Inline</c>, <c>Force New Window</c>, <c>Disable Input</c>, <c>Disabled</c>, or <c>an unrecognised</c>.</returns>
    /// <exception cref="ArgumentException">The value is none of the modes this program knows.</exception>
    public static string Label(this SudoMode mode) =>
        mode switch
        {
            SudoMode.Inline => "Inline",
            SudoMode.ForceNewWindow => "Force New Window",
            SudoMode.DisableInput => "Disable Input",
            SudoMode.Disabled => "Disabled",
            SudoMode.Unknown => "an unrecognised",
            _ => throw new ArgumentException($"unknown sudo mode {mode}", nameof(mode)),
        };
}
