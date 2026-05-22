using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Backend.Database.State.Objects;

namespace Backend.Repositories.Processes;

public static class ProcessSummaryProjectionKeys
{
    public const string DefaultTenantId = "default";
    public const string TenantScopeId = "all";
    public const int DefaultShardCount = 32;

    public static int CalculateShard(Guid processId, int shardCount = DefaultShardCount)
    {
        if (shardCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shardCount), "Shard count must be positive.");
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(processId.ToString("D")), hash);
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash[..4]);
        return (int)(value % (uint)shardCount);
    }

    public static string CreateTimeBucket(DateTimeOffset createdAt) =>
        createdAt.ToUniversalTime().ToString("yyyyMM", CultureInfo.InvariantCulture);

    public static string CreatePartitionKey(
        string tenantId,
        ProcessSummaryProjectionScope scope,
        string scopeId,
        string timeBucket,
        int shard) =>
        $"{tenantId}:{scope}:{scopeId}:{timeBucket}:{shard.ToString("D2", CultureInfo.InvariantCulture)}";

    public static IReadOnlyList<string> EnumerateDescendingTimeBuckets(
        string startBucket,
        DateTimeOffset? createdFrom,
        int maxBucketCount)
    {
        if (maxBucketCount <= 0)
        {
            return [];
        }

        var lowerBound = createdFrom.HasValue
            ? ToMonthIndex(CreateTimeBucket(createdFrom.Value))
            : 1;
        var current = ToMonthIndex(startBucket);
        var buckets = new List<string>(maxBucketCount);

        while (current >= lowerBound && buckets.Count < maxBucketCount)
        {
            buckets.Add(FromMonthIndex(current));
            current--;
        }

        return buckets;
    }

    public static string? GetPreviousTimeBucket(string timeBucket, DateTimeOffset? createdFrom)
    {
        var lowerBound = createdFrom.HasValue
            ? ToMonthIndex(CreateTimeBucket(createdFrom.Value))
            : 1;
        var previous = ToMonthIndex(timeBucket) - 1;
        return previous >= lowerBound ? FromMonthIndex(previous) : null;
    }

    public static bool IsAtOrAfterLowerBound(string timeBucket, DateTimeOffset? createdFrom) =>
        !createdFrom.HasValue || ToMonthIndex(timeBucket) >= ToMonthIndex(CreateTimeBucket(createdFrom.Value));

    public static string ComputeSignature(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }

    private static int ToMonthIndex(string timeBucket)
    {
        if (timeBucket.Length != 6 ||
            !int.TryParse(timeBucket[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(timeBucket[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            year < 1 ||
            month is < 1 or > 12)
        {
            throw new ArgumentException($"Time bucket '{timeBucket}' is invalid. Expected yyyyMM.", nameof(timeBucket));
        }

        return ((year - 1) * 12) + month;
    }

    private static string FromMonthIndex(int monthIndex)
    {
        var zeroBased = monthIndex - 1;
        var year = (zeroBased / 12) + 1;
        var month = (zeroBased % 12) + 1;
        return $"{year.ToString("D4", CultureInfo.InvariantCulture)}{month.ToString("D2", CultureInfo.InvariantCulture)}";
    }
}
