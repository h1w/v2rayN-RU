using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Helper;

public class AppDataMigrationTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "v2raynru_mig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MigrateCopiesTree_WhenTargetMissing()
    {
        var root = NewTempDir();
        try
        {
            var source = Path.Combine(root, "v2rayN");
            var target = Path.Combine(root, "v2rayN-RU");
            Directory.CreateDirectory(Path.Combine(source, "guiConfigs"));
            File.WriteAllText(Path.Combine(source, "guiNConfig.json"), "{}");
            File.WriteAllText(Path.Combine(source, "guiConfigs", "a.txt"), "hello");

            var migrated = Utils.MigrateDirectoryIfTargetMissing(source, target);

            migrated.Should().BeTrue();
            File.Exists(Path.Combine(target, "guiNConfig.json")).Should().BeTrue();
            File.ReadAllText(Path.Combine(target, "guiConfigs", "a.txt")).Should().Be("hello");
            // источник не тронут
            File.Exists(Path.Combine(source, "guiNConfig.json")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MigrateNoop_WhenTargetExists()
    {
        var root = NewTempDir();
        try
        {
            var source = Path.Combine(root, "v2rayN");
            var target = Path.Combine(root, "v2rayN-RU");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "guiNConfig.json"), "SOURCE");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "guiNConfig.json"), "TARGET");

            var migrated = Utils.MigrateDirectoryIfTargetMissing(source, target);

            migrated.Should().BeFalse();
            File.ReadAllText(Path.Combine(target, "guiNConfig.json")).Should().Be("TARGET");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MigrateNoop_WhenSourceMissing()
    {
        var root = NewTempDir();
        try
        {
            var source = Path.Combine(root, "v2rayN");
            var target = Path.Combine(root, "v2rayN-RU");

            var migrated = Utils.MigrateDirectoryIfTargetMissing(source, target);

            migrated.Should().BeFalse();
            Directory.Exists(target).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
