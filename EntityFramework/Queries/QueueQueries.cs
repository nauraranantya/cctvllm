public static class QueueQueries {
    public static IEnumerable<VideoJob> Flagged(this IEnumerable<VideoJob> jobs) =>
        jobs.Where(job => job.Suspicious == "yes" || job.Weapon == "yes");

    public static IEnumerable<VideoJob> InDisplayOrder(this IEnumerable<VideoJob> jobs) =>
        jobs.OrderBy(job => job.Created);
}
