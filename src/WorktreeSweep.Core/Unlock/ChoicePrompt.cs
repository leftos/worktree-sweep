namespace WorktreeSweep.Unlock;

/// <summary>The choices one numbered prompt offers, and what the three answers that are not a number mean.</summary>
/// <param name="Labels">The choices, in the order they are numbered from 1.</param>
/// <param name="DefaultChoice">The choice an empty line means, shown as the prompt's default.</param>
/// <param name="EofChoice">The choice end of input means, such as Done, so a prompt whose terminal is gone answers itself.</param>
/// <param name="FallbackChoice">The choice three answers that are none of the choices mean, such as Skip, so a user who keeps
/// mistyping is not stuck in the prompt.</param>
public sealed record ChoicePrompt(IReadOnlyList<string> Labels, int DefaultChoice, int EofChoice, int FallbackChoice);
