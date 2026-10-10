using Edith.App;

namespace Edith.App.Tests;

public sealed class CloudSettingsTests
{
    [Fact]
    public void ShippedConfig_Loads()
    {
        CloudSettings settings = CloudSettings.Load(@"C:\EDITH\config\cloud.json");

        Assert.Null(CloudSettings.LastError);
        Assert.True(settings.Enabled);
        Assert.Equal(@"C:\EDITH\secrets\gigachat-auth-key.txt", settings.AuthKeyPath);
        Assert.Equal("GigaChat-2-Max", settings.Models[0]);
    }
}
