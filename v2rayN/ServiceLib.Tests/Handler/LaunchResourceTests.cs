using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Handler;

public class LaunchResourceTests
{
    [Theory]
    [InlineData("""{"inbounds":[{"port":31000}]}""", """{"experimental":{"clash_api":{"external_controller":"127.0.0.1:31000"}}}""", false)]
    [InlineData("""{"inbounds":[{"port":31000}]}""", """{"inbounds":[{"listen_port":31001}]}""", true)]
    [InlineData("not-json", "{}", false)]
    public void Main_and_pre_must_have_distinct_resources(string main, string pre, bool expected)
    {
        ChainConfigBuilder.AreLaunchResourcesCompatible([main, pre]).Should().Be(expected);
    }
}
