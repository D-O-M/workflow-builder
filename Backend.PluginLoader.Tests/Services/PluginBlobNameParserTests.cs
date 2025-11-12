using System;
using Backend.PluginLoader.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using TUnit.Core;

namespace Backend.PluginLoader.Tests.Services;

public class PluginBlobNameParserTests
{
    private readonly PluginBlobNameParser _sut;

    public PluginBlobNameParserTests()
    {
        var logger = Mock.Of<ILogger<PluginBlobNameParser>>();
        _sut = new PluginBlobNameParser(logger);
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
