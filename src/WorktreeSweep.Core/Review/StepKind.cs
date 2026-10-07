namespace WorktreeSweep.Review;

/// <summary>Which question a <see cref="ReviewStep"/> asks.</summary>
public enum StepKind
{
    /// <summary>Removing the pick loses work, or leaves a registration behind.</summary>
    Loss,

    /// <summary>The pick is a link; only the link goes.</summary>
    Link,

    /// <summary>The pick cannot go to the Recycle Bin.</summary>
    Permanent,

    /// <summary>The pick's branch can be deleted once it is gone.</summary>
    Branch,
}
