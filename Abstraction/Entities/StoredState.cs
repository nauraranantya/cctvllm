public sealed record StoredState(ModelSettings Settings, List<VideoJob> Jobs) {
    public string? LinkedFolder { get; init; }
}
