namespace WorktreeSweep.Report;

/// <summary>One labelled field of the picker's detail pane.</summary>
/// <param name="Label">What the field is, such as <c>State</c>.</param>
/// <param name="Text">The field's text.</param>
public sealed record DetailField(string Label, string Text);
