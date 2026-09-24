public interface IQueueRepository {
    object Snapshot();
    VideoJob Get(string id);
    void Add(VideoJob job);
    void Update(string id, Func<VideoJob, VideoJob> update);
    void SetSettings(ModelSettings value);
    void SetRunning(bool value);
    (VideoJob Job, ModelSettings Settings)? Take();
}
