namespace Backend.Models.Versioning;

public static class SemanticVersionUtility
{
    public static int CompareDescending(Version? left, Version? right) =>
        Comparer<Version>.Default.Compare(right ?? new Version(0, 0), left ?? new Version(0, 0));

    public static bool IsSameLane(Version version, int major, int minor) =>
        version.Major == major && version.Minor == minor;

    public static int GetPatch(Version version) => version.Build >= 0 ? version.Build : 0;
}
