namespace WorktreeSweep.Review;

/// <summary>One question of a <see cref="ReviewSession"/>.</summary>
/// <param name="Pick">The pick it is about, as an index into the candidates the session was given.</param>
/// <param name="Kind">Which question it is.</param>
/// <param name="Body">What the answer is about, shown before the question.</param>
/// <param name="Question">The short question, naming no path.</param>
/// <param name="DefaultAnswer">The answer Enter gives.</param>
public sealed record ReviewStep(int Pick, StepKind Kind, string Body, string Question, bool DefaultAnswer);
