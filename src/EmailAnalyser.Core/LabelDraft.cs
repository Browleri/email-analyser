using EmailAnalyser.Core.Storage;

namespace EmailAnalyser.Core;

/// <summary>What to do with a label the user has changed but not saved.</summary>
public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// The labelling form's values and the values it was loaded with, so that the view can
/// tell when a change would be lost.
/// </summary>
public sealed class LabelDraft
{
    private (string?, string?, string?, DateOnly?, string?) _loaded;

    public string? Action { get; set; }
    public string? Urgency { get; set; }
    public string? Topic { get; set; }
    public DateOnly? Deadline { get; set; }

    /// <summary>The optional reason in a few words; blank counts as no note.</summary>
    public string? Note { get; set; }

    public bool IsDirty => Current != _loaded;

    public bool CanSave => Action is not null && Urgency is not null && Topic is not null;

    private (string?, string?, string?, DateOnly?, string?) Current => (Action, Urgency, Topic, Deadline, UserLabel.CleanNote(Note));

    /// <summary>
    /// Starts from the user's label, otherwise from the visible AI answer (null in blind
    /// mode), otherwise empty. Prefilled AI values do not count as a change.
    /// </summary>
    public void Load(UserLabel? label, Assessment? visibleAi)
    {
        Action = label?.Action ?? visibleAi?.Action;
        Urgency = label?.Urgency ?? visibleAi?.Urgency;
        Topic = label?.Topic ?? visibleAi?.Topic;
        Deadline = label is not null ? label.Deadline : visibleAi?.Deadline;
        Note = label?.Note;
        _loaded = Current;
    }

    /// <summary>After saving or deliberately discarding: the current values are the new baseline.</summary>
    public void MarkClean() => _loaded = Current;

    public UserLabel ToLabel() => CanSave
        ? new UserLabel(Action!, Urgency!, Topic!, Deadline, Note: UserLabel.CleanNote(Note))
        : throw new InvalidOperationException("Merkintä on kesken.");
}
