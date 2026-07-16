using AwesomeAssertions;
using ServiceLib.Helper;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class RemarkResolutionTests
{
    // --- ResolveRemarkOnSave ---

    [Fact]
    public void OnSave_EmptyEntered_UsesHost_AndMarksAuto()
    {
        var (remarks, auto) = SubscriptionInfoHelper.ResolveRemarkOnSave("", originalRemarks: "", wasAuto: false, urlHost: "sub.example.com");
        remarks.Should().Be("sub.example.com");
        auto.Should().BeTrue();
    }

    [Fact]
    public void OnSave_TypedNewName_MarksManual()
    {
        var (remarks, auto) = SubscriptionInfoHelper.ResolveRemarkOnSave("My VPN", originalRemarks: "", wasAuto: false, urlHost: "sub.example.com");
        remarks.Should().Be("My VPN");
        auto.Should().BeFalse();
    }

    [Fact]
    public void OnSave_UnchangedAutoName_StaysAuto()
    {
        var (remarks, auto) = SubscriptionInfoHelper.ResolveRemarkOnSave("sub.example.com", originalRemarks: "sub.example.com", wasAuto: true, urlHost: "sub.example.com");
        remarks.Should().Be("sub.example.com");
        auto.Should().BeTrue();
    }

    [Fact]
    public void OnSave_ChangedName_BecomesManual()
    {
        var (remarks, auto) = SubscriptionInfoHelper.ResolveRemarkOnSave("Renamed", originalRemarks: "sub.example.com", wasAuto: true, urlHost: "sub.example.com");
        remarks.Should().Be("Renamed");
        auto.Should().BeFalse();
    }

    // --- ResolveRemarkOnUpdate ---

    [Fact]
    public void OnUpdate_Manual_LeavesRemarksUnchanged()
    {
        SubscriptionInfoHelper.ResolveRemarkOnUpdate("My VPN", autoRemark: false, profileTitle: "Server Title", urlHost: "sub.example.com")
            .Should().Be("My VPN");
    }

    [Fact]
    public void OnUpdate_Auto_UsesProfileTitle()
    {
        SubscriptionInfoHelper.ResolveRemarkOnUpdate("sub.example.com", autoRemark: true, profileTitle: "Server Title", urlHost: "sub.example.com")
            .Should().Be("Server Title");
    }

    [Fact]
    public void OnUpdate_Auto_NoProfileTitle_KeepsCurrentNonEmpty()
    {
        SubscriptionInfoHelper.ResolveRemarkOnUpdate("sub.example.com", autoRemark: true, profileTitle: null, urlHost: "sub.example.com")
            .Should().Be("sub.example.com");
    }

    [Fact]
    public void OnUpdate_Auto_NoProfileTitle_EmptyCurrent_UsesHost()
    {
        SubscriptionInfoHelper.ResolveRemarkOnUpdate("", autoRemark: true, profileTitle: null, urlHost: "sub.example.com")
            .Should().Be("sub.example.com");
    }
}
