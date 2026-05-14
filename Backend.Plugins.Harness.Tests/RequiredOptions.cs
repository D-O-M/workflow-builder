using Backend.Plugins.Configuration;

namespace Backend.Plugins.Configuration.Tests;

public sealed class RequiredOptions
{
    public required string RequiredName { get; init; }
    public string? OptionalName { get; init; }
    public required Secret<string> Token { get; init; }
}
