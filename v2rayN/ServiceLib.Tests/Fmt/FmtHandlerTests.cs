using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Handler.Fmt;
using ServiceLib.Models;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Fmt;

public class FmtHandlerTests
{
    /// <summary>
    /// One profile factory per protocol that <see cref="FmtHandler.GetShareUri" /> can export.
    /// The suite below asserts that this map and <see cref="Global.ProtocolShares" /> agree, so a
    /// newly exportable protocol cannot be added without a round-trip case.
    /// </summary>
    private static readonly Dictionary<EConfigType, Func<ProfileItem>> ShareProfileFactories = new()
    {
        [EConfigType.VMess] = CreateVmessProfile,
        [EConfigType.Shadowsocks] = CreateShadowsocksProfile,
        [EConfigType.SOCKS] = CreateSocksProfile,
        [EConfigType.VLESS] = CreateVlessProfile,
        [EConfigType.Trojan] = CreateTrojanProfile,
        [EConfigType.Hysteria2] = CreateHysteria2Profile,
        [EConfigType.TUIC] = CreateTuicProfile,
        [EConfigType.WireGuard] = CreateWireguardProfile,
        [EConfigType.Anytls] = CreateAnytlsProfile,
        [EConfigType.Naive] = () => CreateNaiveProfile(false),
    };

    [Fact]
    public void ShareUriSuite_ShouldCoverAndRoundTripEveryExportableProtocol()
    {
        var uncovered = string.Join(", ", Global.ProtocolShares.Keys.Except(ShareProfileFactories.Keys));
        var unexpected = string.Join(", ", ShareProfileFactories.Keys.Except(Global.ProtocolShares.Keys));

        uncovered.Should().Be(string.Empty);
        unexpected.Should().Be(string.Empty);

        foreach (var (configType, factory) in ShareProfileFactories)
        {
            var source = factory();

            var resolved = ExportThenImport(source);

            resolved.ConfigType.Should().Be(configType);
            resolved.Address.Should().Be(source.Address);
            resolved.Port.Should().Be(source.Port);
        }
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Vmess_ShouldRoundTripBasicFields()
    {
        var source = CreateVmessProfile();

        var resolved = ExportThenImport(source);

        resolved.ConfigType.Should().Be(EConfigType.VMess);
        resolved.Remarks.Should().Be(source.Remarks);
        resolved.Address.Should().Be(source.Address);
        resolved.Port.Should().Be(source.Port);
        resolved.Password.Should().Be(source.Password);
        resolved.GetProtocolExtra().AlterId.Should().Be(source.GetProtocolExtra().AlterId);
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Vless_ShouldRoundTripBasicFields()
    {
        var source = CreateVlessProfile();

        var resolved = ExportThenImport(source);

        resolved.ConfigType.Should().Be(EConfigType.VLESS);
        resolved.Remarks.Should().Be(source.Remarks);
        resolved.Address.Should().Be(source.Address);
        resolved.Port.Should().Be(source.Port);
        resolved.Password.Should().Be(source.Password);
        resolved.GetProtocolExtra().VlessEncryption.Should().Be(Global.None);
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Shadowsocks_ShouldRoundTripBasicFields()
    {
        var source = CreateShadowsocksProfile();

        var resolved = ExportThenImport(source);

        resolved.ConfigType.Should().Be(EConfigType.Shadowsocks);
        resolved.Remarks.Should().Be(source.Remarks);
        resolved.Address.Should().Be(source.Address);
        resolved.Port.Should().Be(source.Port);
        resolved.Password.Should().Be(source.Password);
        resolved.GetProtocolExtra().SsMethod.Should().Be(source.GetProtocolExtra().SsMethod);
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Socks_ShouldRoundTripBasicFields()
    {
        var source = CreateSocksProfile();

        var resolved = ExportThenImport(source);

        resolved.ConfigType.Should().Be(EConfigType.SOCKS);
        resolved.Remarks.Should().Be(source.Remarks);
        resolved.Address.Should().Be(source.Address);
        resolved.Port.Should().Be(source.Port);
        resolved.Username.Should().Be(source.Username);
        resolved.Password.Should().Be(source.Password);
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Trojan_ShouldRoundTripBasicFields()
    {
        var source = CreateTrojanProfile();

        var resolved = ExportThenImport(source);

        AssertCommonShareFields(source, resolved);
        AssertRawTransportFields(source, resolved);
        resolved.Password.Should().Be(source.Password);
        resolved.Sni.Should().Be(source.Sni);
        resolved.GetProtocolExtra().Flow.Should().Be(source.GetProtocolExtra().Flow);
        resolved.GetAllowInsecure().Should().BeTrue();

        // Trojan is the one exporter that writes both spellings of the flag.
        AssertExportContains(source, "allowInsecure=1", "insecure=1");
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Tuic_ShouldRoundTripUserInfoAndCongestionControl()
    {
        var source = CreateTuicProfile();

        var resolved = ExportThenImport(source);

        AssertCommonShareFields(source, resolved);
        resolved.Username.Should().Be(source.Username);
        resolved.Password.Should().Be(source.Password);
        resolved.Sni.Should().Be(source.Sni);
        resolved.Alpn.Should().Be(source.Alpn);
        resolved.GetProtocolExtra().CongestionControl.Should()
            .Be(source.GetProtocolExtra().CongestionControl);
        resolved.GetAllowInsecure().Should().BeTrue();

        AssertExportContains(source, "allow_insecure=1");
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Anytls_ShouldRoundTripBasicFields()
    {
        var source = CreateAnytlsProfile();

        var resolved = ExportThenImport(source);

        AssertCommonShareFields(source, resolved);
        AssertRawTransportFields(source, resolved);
        resolved.Password.Should().Be(source.Password);
        resolved.Sni.Should().Be(source.Sni);
        resolved.Alpn.Should().Be(source.Alpn);
        resolved.GetAllowInsecure().Should().BeTrue();

        AssertExportContains(source, "insecure=1");
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Hysteria2_ShouldRoundTripObfsAndNormalizePortRange()
    {
        var source = CreateHysteria2Profile();

        var resolved = ExportThenImport(source);
        var sourceExtra = source.GetProtocolExtra();
        var resolvedExtra = resolved.GetProtocolExtra();

        AssertCommonShareFields(source, resolved);
        resolved.Password.Should().Be(source.Password);
        resolved.Sni.Should().Be(source.Sni);
        resolved.Alpn.Should().Be(source.Alpn);
        resolved.EchConfigList.Should().Be(source.EchConfigList);
        resolved.GetAllowInsecure().Should().BeTrue();
        resolvedExtra.SalamanderPass.Should().Be(sourceExtra.SalamanderPass);

        // Hysteria2Fmt stores a port range internally as "5000:6000" and emits the URI form.
        resolvedExtra.Ports.Should().Be("5000-6000");

        AssertExportContains(source, "insecure=1", "obfs=salamander", "mport=5000-6000");
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Wireguard_ShouldRoundTripKeysAndInterface()
    {
        var source = CreateWireguardProfile();

        var resolved = ExportThenImport(source);
        var extra = resolved.GetProtocolExtra();
        var sourceExtra = source.GetProtocolExtra();

        AssertCommonShareFields(source, resolved);
        resolved.Password.Should().Be(source.Password);
        extra.WgPublicKey.Should().Be(sourceExtra.WgPublicKey);
        extra.WgPresharedKey.Should().Be(sourceExtra.WgPresharedKey);
        extra.WgReserved.Should().Be(sourceExtra.WgReserved);
        extra.WgInterfaceAddress.Should().Be(sourceExtra.WgInterfaceAddress);
        extra.WgMtu.Should().Be(sourceExtra.WgMtu);
    }

    [Fact]
    public void GetShareUri_Wireguard_ShouldEncodeKeysAndBracketIpv6()
    {
        var source = CreateWireguardProfile();

        // Base64 keys carry '/', '+' and '=', and the address is an IPv6 literal: both have to
        // survive the wire form, which a round trip through the same encoder would not prove.
        AssertExportContains(
            source,
            Uri.EscapeDataString(source.Password),
            Uri.EscapeDataString(source.GetProtocolExtra().WgPublicKey ?? string.Empty),
            "@[2001:db8::40]:51820");
    }

    [Fact]
    public void GetShareUriAndResolveConfig_Naive_ShouldRoundTripCredentialsOverHttps()
    {
        var source = CreateNaiveProfile(false);

        var resolved = ExportThenImport(source, Global.NaiveHttpsProtocolShare);

        AssertCommonShareFields(source, resolved);
        AssertRawTransportFields(source, resolved);
        resolved.Username.Should().Be(source.Username);
        resolved.Password.Should().Be(source.Password);
        resolved.GetProtocolExtra().InsecureConcurrency.Should()
            .Be(source.GetProtocolExtra().InsecureConcurrency);

        // NaiveFmt only ever sets this flag on the quic branch, so the https branch leaves it
        // unset rather than false - assert "is not quic" instead of an explicit false.
        (resolved.GetProtocolExtra().NaiveQuic == true).Should().BeFalse();
    }

    [Fact]
    public void GetShareUriAndResolveConfig_NaiveQuic_ShouldRoundTripQuicScheme()
    {
        var source = CreateNaiveProfile(true);

        var resolved = ExportThenImport(source, Global.NaiveQuicProtocolShare);

        AssertCommonShareFields(source, resolved);
        AssertRawTransportFields(source, resolved);
        resolved.Username.Should().Be(source.Username);
        resolved.Password.Should().Be(source.Password);
        resolved.GetProtocolExtra().InsecureConcurrency.Should()
            .Be(source.GetProtocolExtra().InsecureConcurrency);
        resolved.GetProtocolExtra().NaiveQuic.Should().BeTrue();
    }

    [Theory]
    [InlineData("p:a@ss#% +/=")]
    [InlineData("пароль 東京")]
    public void GetShareUriAndResolveConfig_Trojan_ShouldRoundTripEncodedCredentials(string password)
    {
        var source = CreateTrojanProfile();
        source.Password = password;
        source.Remarks = "Trojan — тест 東京 #1";

        var resolved = ExportThenImport(source);

        resolved.Password.Should().Be(password);
        resolved.Remarks.Should().Be(source.Remarks);
    }

    [Fact]
    public void ResolveConfig_UnsupportedProtocol_ShouldReturnNull()
    {
        var resolved = FmtHandler.ResolveConfig("not-a-share-uri", out var msg);

        resolved.Should().BeNull();
        msg.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetShareUri_UnsupportedConfigType_ShouldReturnNull()
    {
        var item = new ProfileItem { ConfigType = EConfigType.PolicyGroup, Remarks = "group", };

        var uri = FmtHandler.GetShareUri(item);

        uri.Should().BeNull();
    }

    private static void AssertCommonShareFields(ProfileItem source, ProfileItem resolved)
    {
        resolved.ConfigType.Should().Be(source.ConfigType);
        resolved.Remarks.Should().Be(source.Remarks);
        resolved.Address.Should().Be(source.Address);
        resolved.Port.Should().Be(source.Port);
    }

    /// <summary>
    /// Only for protocols whose exporter goes through the shared transport query
    /// (<c>security</c>, <c>type</c>, <c>headerType</c>). TUIC, Hysteria2 and WireGuard do not.
    /// </summary>
    private static void AssertRawTransportFields(ProfileItem source, ProfileItem resolved)
    {
        resolved.Network.Should().Be(source.Network);
        resolved.StreamSecurity.Should().Be(source.StreamSecurity);
        resolved.GetTransportExtra().RawHeaderType.Should()
            .Be(source.GetTransportExtra().RawHeaderType);
    }

    /// <summary>
    /// Asserts on the wire form itself. A round trip cannot catch an exporter and an importer that
    /// agree on the wrong spelling of a parameter, and the insecure flag is spelled differently by
    /// every protocol.
    /// </summary>
    private static void AssertExportContains(ProfileItem source, params string[] expectedFragments)
    {
        var uri = FmtHandler.GetShareUri(source);

        uri.Should().NotBeNull();

        foreach (var fragment in expectedFragments)
        {
            uri!.Contains(fragment, StringComparison.Ordinal).Should()
                .BeTrue($"uri: {uri}, expected fragment: {fragment}");
        }
    }

    private static string ExpectedShareScheme(ProfileItem item)
    {
        if (item.ConfigType != EConfigType.Naive)
        {
            return Global.ProtocolShares[item.ConfigType];
        }

        // NaiveFmt never emits the "naive://" prefix that Global.ProtocolShares records for the
        // type; that entry is only read when importing.
        return item.GetProtocolExtra().NaiveQuic == true
            ? Global.NaiveQuicProtocolShare
            : Global.NaiveHttpsProtocolShare;
    }

    private static ProfileItem ExportThenImport(ProfileItem source)
    {
        return ExportThenImport(source, ExpectedShareScheme(source));
    }

    private static ProfileItem ExportThenImport(ProfileItem source, string expectedPrefix)
    {
        var uri = FmtHandler.GetShareUri(source);

        uri.Should().NotBeNull();
        uri.Should().NotBeEmpty();
        uri!.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        var resolved = FmtHandler.ResolveConfig(uri, out var msg);

        resolved.Should().NotBeNull($"uri: {uri}, msg: {msg}");
        return resolved!;
    }

    private static ProfileItem CreateVmessProfile()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.VMess,
            Remarks = "vmess demo",
            Address = "example.com",
            Port = 443,
            Password = Guid.NewGuid().ToString(),
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
        };

        item.SetProtocolExtra(new ProtocolExtraItem { AlterId = "0", VmessSecurity = Global.DefaultSecurity, });
        item.SetTransportExtra(new TransportExtraItem { RawHeaderType = Global.None, });

        return item;
    }

    private static ProfileItem CreateVlessProfile()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.VLESS,
            Remarks = "vless demo",
            Address = "vless.example",
            Port = 8443,
            Password = Guid.NewGuid().ToString(),
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
        };

        item.SetProtocolExtra(new ProtocolExtraItem { VlessEncryption = Global.None, });
        item.SetTransportExtra(new TransportExtraItem { RawHeaderType = Global.None, });

        return item;
    }

    private static ProfileItem CreateShadowsocksProfile()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.Shadowsocks,
            Remarks = "ss demo",
            Address = "1.2.3.4",
            Port = 8388,
            Password = "pass123",
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
        };

        item.SetProtocolExtra(new ProtocolExtraItem { SsMethod = "aes-128-gcm", });
        item.SetTransportExtra(new TransportExtraItem { RawHeaderType = Global.None, });

        return item;
    }

    private static ProfileItem CreateSocksProfile()
    {
        return new ProfileItem
        {
            ConfigType = EConfigType.SOCKS,
            Remarks = "socks demo",
            Address = "127.0.0.1",
            Port = 1080,
            Username = "user",
            Password = "pass",
        };
    }

    private static ProfileItem CreateTrojanProfile()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.Trojan,
            Remarks = "trojan demo",
            Address = "trojan.example",
            Port = 443,
            Password = "trojan-pass",
            Network = nameof(ETransport.raw),
            StreamSecurity = Global.StreamSecurity,
            Sni = "sni.trojan.example",
            AllowInsecure = Global.StringTrue,
        };

        item.SetProtocolExtra(new ProtocolExtraItem { Flow = Global.Flows[1], });
        item.SetTransportExtra(new TransportExtraItem { RawHeaderType = Global.None, });

        return item;
    }

    private static ProfileItem CreateTuicProfile()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.TUIC,
            Remarks = "tuic demo",
            Address = "tuic.example",
            Port = 8443,
            // A colon separates the two halves of the TUIC user info, so it cannot appear in the
            // uuid; a fixed value also keeps a failure reproducible.
            Username = "01234567-89ab-cdef-0123-456789abcdef",
            Password = "tuic-pass",
            Sni = "sni.tuic.example",
            Alpn = "h3",
            AllowInsecure = Global.StringTrue,
        };

        item.SetProtocolExtra(new ProtocolExtraItem { CongestionControl = "bbr", });

        return item;
    }

    private static ProfileItem CreateAnytlsProfile()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.Anytls,
            Remarks = "anytls demo",
            Address = "anytls.example",
            Port = 8443,
            Password = "anytls-pass",
            Network = nameof(ETransport.raw),
            StreamSecurity = Global.StreamSecurity,
            Sni = "sni.anytls.example",
            Alpn = "h2,http/1.1",
            AllowInsecure = Global.StringTrue,
        };

        item.SetTransportExtra(new TransportExtraItem { RawHeaderType = Global.None, });

        return item;
    }

    private static ProfileItem CreateHysteria2Profile()
    {
        // CertSha is deliberately left unset: the importer turns AllowInsecure on by itself when a
        // pinSHA256 is present, which would mask an exporter that stopped emitting insecure=1.
        var item = new ProfileItem
        {
            ConfigType = EConfigType.Hysteria2,
            Remarks = "hysteria2 demo",
            Address = "hy2.example",
            Port = 8443,
            Password = "demo-user:demo-pass",
            Sni = "sni.hy2.example",
            Alpn = "h3",
            EchConfigList = "AAj+DQAEAAAAAA==",
            AllowInsecure = Global.StringTrue,
        };

        item.SetProtocolExtra(new ProtocolExtraItem
        {
            SalamanderPass = "salamander-pass",
            Ports = "5000:6000",
        });

        return item;
    }

    private static string CreateWireguardKey(byte value)
    {
        return Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray());
    }

    private static ProfileItem CreateWireguardProfile()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.WireGuard,
            Remarks = "WireGuard — тест 東京 #1",
            Address = "2001:db8::40",
            Port = 51820,
            Password = CreateWireguardKey(0xFE),
        };

        item.SetProtocolExtra(new ProtocolExtraItem
        {
            WgPublicKey = CreateWireguardKey(0xFD),
            WgPresharedKey = CreateWireguardKey(0xFC),
            WgReserved = "1,2,255",
            WgInterfaceAddress = "10.0.0.2/32,fd00::2/128",
            WgMtu = 1420,
        });

        return item;
    }

    private static ProfileItem CreateNaiveProfile(bool quic)
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.Naive,
            Remarks = quic ? "naive quic demo" : "naive https demo",
            Address = "naive.example",
            Port = 443,
            Username = "naive-user",
            Password = "päss:word@/?#&=+ 東京",
            Network = nameof(ETransport.raw),
            StreamSecurity = Global.None,
        };

        item.SetProtocolExtra(new ProtocolExtraItem { NaiveQuic = quic, InsecureConcurrency = 4, });
        item.SetTransportExtra(new TransportExtraItem { RawHeaderType = Global.None, });

        return item;
    }
}
