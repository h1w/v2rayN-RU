using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Manager;

public class CoreManagerCleanupTests
{
    [Fact]
    public void CleanupChainConfigFiles_DeletesOnlyConfigChainFiles_LeavesOthersIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"v2rayn-ru-test-chaincleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var chain0 = Path.Combine(dir, "configChain0.json");
            var chain1 = Path.Combine(dir, "configChain1.json");
            var config = Path.Combine(dir, "config.json");
            var configPre = Path.Combine(dir, "configPre.json");
            var configTest5 = Path.Combine(dir, "configTest5.json");

            File.WriteAllText(chain0, "{}");
            File.WriteAllText(chain1, "{}");
            File.WriteAllText(config, "{}");
            File.WriteAllText(configPre, "{}");
            File.WriteAllText(configTest5, "{}");

            CoreManager.CleanupChainConfigFiles(dir);

            // Ровно два configChain*.json удалены...
            File.Exists(chain0).Should().BeFalse();
            File.Exists(chain1).Should().BeFalse();
            // ...остальные три (в т.ч. похожие по имени config*.json) не тронуты.
            File.Exists(config).Should().BeTrue();
            File.Exists(configPre).Should().BeTrue();
            File.Exists(configTest5).Should().BeTrue();
            Directory.GetFiles(dir).Should().HaveCount(3);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
