namespace Backend.Interfaces.Repositories;

public static class ProcessStepDispatchSchedule
{
    public static long CalculateDueBucket(DateTimeOffset timestamp, TimeSpan dueBucketDuration)
    {
        var durationMilliseconds = Math.Max(1, (long)Math.Ceiling(dueBucketDuration.TotalMilliseconds));
        return timestamp.ToUnixTimeMilliseconds() / durationMilliseconds;
    }

    public static int CalculateShard(string processStepId, int shardCount)
    {
        var normalizedShardCount = Math.Max(1, shardCount);
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(processStepId);
        return (hash & int.MaxValue) % normalizedShardCount;
    }

    public static string CreateMessageId(Guid stepId, long generation) =>
        generation == 0 ? stepId.ToString("N") : $"{stepId:N}-{generation}";

    public static string CreateIntentId(
        string processStepId,
        long dueBucket,
        int attemptCount,
        long generation = 0) =>
        generation == 0
            ? $"{processStepId}:{dueBucket}:{attemptCount}"
            : $"{processStepId}:g{generation}:{dueBucket}:{attemptCount}";
}
