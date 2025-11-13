using System;
using Backend.Interfaces.Utilities;
using Backend.PluginLoader.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.PluginLoader.Tests.Services;

public class PluginBlobNameParsingUtilityTests
{
    private readonly IPluginBlobNameParsingUtility _sut;

    public PluginBlobNameParsingUtilityTests()
    {
        _sut = new PluginBlobNameParsingUtility(new NullLogger<PluginBlobNameParsingUtility>());
    }

    [Test]
    public void Parse_WithValidBlobName_ReturnsExpectedInfo()
    {
        var result = _sut.Parse("ExamplePlugin_1.2.3.zip");

        result.BlobName.ShouldBe("ExamplePlugin_1.2.3.zip");
        result.PluginName.ShouldBe("ExamplePlugin");
        result.Version.ShouldBe(new Version(1, 2, 3));
    }

    [Test]
    public void Parse_WithInvalidBlobName_ThrowsFormatException()
    {
        Should.Throw<FormatException>(() => _sut.Parse("invalid.zip"));
    }
}
