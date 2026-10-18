namespace WorktreeSweep.ViewModels;

/// <summary>One of the scan's three steps on the Scanning screen.</summary>
/// <param name="Title">What the step does.</param>
/// <param name="Detail">How far it has got.</param>
/// <param name="State">Whether it waits, runs or is done.</param>
public sealed record ScanStepRow(string Title, string Detail, StepState State);
