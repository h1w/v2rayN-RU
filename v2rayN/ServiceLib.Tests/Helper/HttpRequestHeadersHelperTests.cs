using AwesomeAssertions;
using ServiceLib.Helper;
using ServiceLib.Models.Entities;
using SQLite;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class HttpRequestHeadersHelperTests
{
    [Fact]
    public void TryParse_ShouldAcceptEmptySettingsForExistingSubscriptions()
    {
        foreach (var json in new string?[] { null, "", " \r\n ", "{}" })
        {
            HttpRequestHeadersHelper.TryParse(json, out var headers).Should().BeTrue();
            headers.Count.Should().Be(0);
        }
    }

    [Fact]
    public void TryParse_ShouldPreserveValuesAndUseCaseInsensitiveNames()
    {
        const string json = """
            {
              "X-hwid": "my_test_device",
              "Authorization": "Bearer test:token",
              "accept": "application/json",
              "Content-Type": "application/json",
              "X-Empty": ""
            }
            """;

        HttpRequestHeadersHelper.TryParse(json, out var headers).Should().BeTrue();
        headers["x-HWID"].Should().Be("my_test_device");
        headers["AUTHORIZATION"].Should().Be("Bearer test:token");
        headers["Accept"].Should().Be("application/json");
        headers["Content-Type"].Should().Be("application/json");
        headers["X-Empty"].Should().Be("");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"X-Test\": 1}")]
    [InlineData("{\"X-Test\": null}")]
    [InlineData("{\"X-Test\": [\"one\", \"two\"]}")]
    [InlineData("{\"X-Test\": \"one\", \"X-Test\": \"two\"}")]
    [InlineData("{\"Accept\": \"one\", \"accept\": \"two\"}")]
    [InlineData("{\"Bad Header\": \"value\"}")]
    [InlineData("{\"Bad:Header\": \"value\"}")]
    [InlineData("{\"\": \"value\"}")]
    [InlineData("{\"X-Test\": \"one\\r\\nInjected: two\"}")]
    [InlineData("{\"X-Test\": \"one\\nInjected: two\"}")]
    [InlineData("{\"X-Test\": \"one\\u0000two\"}")]
    public void TryParse_ShouldRejectInvalidHeadersWithoutReturningPartialSettings(string json)
    {
        HttpRequestHeadersHelper.TryParse(json, out var headers).Should().BeFalse();
        headers.Count.Should().Be(0);
    }

    [Fact]
    public void RequestHeaders_ShouldSurviveDatabaseMigrationAndEditing()
    {
        using var database = new SQLiteConnection(":memory:", false);
        database.Execute("CREATE TABLE SubItem (Id TEXT PRIMARY KEY, Remarks TEXT, Url TEXT)");
        database.Execute("INSERT INTO SubItem (Id, Remarks, Url) VALUES (?, ?, ?)", "existing", "Existing", "https://example.com/sub");
        database.CreateTable<SubItem>();

        var item = database.Find<SubItem>("existing");
        HttpRequestHeadersHelper.TryParse(item.RequestHeaders, out var oldHeaders).Should().BeTrue();
        oldHeaders.Count.Should().Be(0);

        item.RequestHeaders = "{\"X-hwid\":\"my_device\"}";
        database.Update(item);
        database.Find<SubItem>(item.Id).RequestHeaders.Should().Be(item.RequestHeaders);

        item.RequestHeaders = "";
        database.Update(item);
        database.Find<SubItem>(item.Id).RequestHeaders.Should().Be("");
    }
}
