namespace Backend.Interfaces.Repositories;

public static class ProcessStepDispatchIntentKeys
{
    public static string CreateDuePartitionKey(long dueBucket, int shard) =>
        $"due:{dueBucket:D20}:{shard:D4}";

    public static string CreateCursorPartitionKey(int shard) =>
        $"cursor:{shard:D4}";

    public static string CreateCursorId(int shard) =>
        CreateCursorPartitionKey(shard);
}
