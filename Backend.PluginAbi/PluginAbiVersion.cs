namespace Backend.PluginAbi;

public readonly record struct PluginAbiVersion(int Major, int Minor)
{
    public override string ToString() => $"{Major}.{Minor}";
}
