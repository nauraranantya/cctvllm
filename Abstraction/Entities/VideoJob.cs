public sealed record VideoJob {
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    // Set only for videos discovered in the linked folder. The file stays where the user keeps
    // it and is read in place; nothing is copied into data/, and removing the job never touches it.
    public string? SourcePath { get; init; }
    public string Status { get; init; } = "queued";
    public string Stage { get; init; } = "Queued";
    public double Duration { get; init; }
    public double Fps { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public string? Description { get; init; }
    public string? Suspicious { get; init; }
    public string? Weapon { get; init; }
    public bool WeaponEnabled { get; init; } = true;
    public string? Error { get; init; }
    public string? RawText { get; init; }
    public string? Model { get; init; }
    public string? BackendMode { get; init; }
    public bool PersonLabels { get; init; }
    public double[] Timestamps { get; init; } = [];
    public double? Elapsed { get; init; }
}
// LinkedFolder is a property rather than a constructor parameter so a state.json written
// before this feature existed still deserializes, with the folder simply absent.
