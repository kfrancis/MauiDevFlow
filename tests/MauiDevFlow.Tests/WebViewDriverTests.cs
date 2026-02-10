using MauiDevFlow.Driver;

namespace MauiDevFlow.Tests;

public class AgentClientTests
{
    [Fact]
    public void Constructor_DefaultValues()
    {
        using var client = new AgentClient();
        Assert.Equal("http://localhost:9223", client.BaseUrl);
    }

    [Fact]
    public void Constructor_CustomHostAndPort()
    {
        using var client = new AgentClient("192.168.1.100", 8080);
        Assert.Equal("http://192.168.1.100:8080", client.BaseUrl);
    }

    [Fact]
    public async Task GetStatus_WhenAgentNotRunning_ReturnsNull()
    {
        using var client = new AgentClient("localhost", 19999);
        var status = await client.GetStatusAsync();
        Assert.Null(status);
    }

    [Fact]
    public async Task GetTree_WhenAgentNotRunning_ReturnsEmptyList()
    {
        using var client = new AgentClient("localhost", 19999);
        var tree = await client.GetTreeAsync();
        Assert.Empty(tree);
    }

    [Fact]
    public async Task Query_WhenAgentNotRunning_ReturnsEmptyList()
    {
        using var client = new AgentClient("localhost", 19999);
        var results = await client.QueryAsync(type: "Button");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Screenshot_WhenAgentNotRunning_ReturnsNull()
    {
        using var client = new AgentClient("localhost", 19999);
        var data = await client.ScreenshotAsync();
        Assert.Null(data);
    }

    [Fact]
    public async Task Tap_WhenAgentNotRunning_ReturnsFalse()
    {
        using var client = new AgentClient("localhost", 19999);
        var result = await client.TapAsync("test-id");
        Assert.False(result);
    }

    [Fact]
    public async Task Fill_WhenAgentNotRunning_ReturnsFalse()
    {
        using var client = new AgentClient("localhost", 19999);
        var result = await client.FillAsync("test-id", "hello");
        Assert.False(result);
    }

    [Fact]
    public async Task Clear_WhenAgentNotRunning_ReturnsFalse()
    {
        using var client = new AgentClient("localhost", 19999);
        var result = await client.ClearAsync("test-id");
        Assert.False(result);
    }

    [Fact]
    public async Task Focus_WhenAgentNotRunning_ReturnsFalse()
    {
        using var client = new AgentClient("localhost", 19999);
        var result = await client.FocusAsync("test-id");
        Assert.False(result);
    }
}

public class AppDriverFactoryTests
{
    [Theory]
    [InlineData("maccatalyst", typeof(MacCatalystAppDriver))]
    [InlineData("mac", typeof(MacCatalystAppDriver))]
    [InlineData("catalyst", typeof(MacCatalystAppDriver))]
    [InlineData("android", typeof(AndroidAppDriver))]
    [InlineData("ios", typeof(iOSSimulatorAppDriver))]
    [InlineData("iossimulator", typeof(iOSSimulatorAppDriver))]
    [InlineData("windows", typeof(WindowsAppDriver))]
    [InlineData("win", typeof(WindowsAppDriver))]
    [InlineData("winui", typeof(WindowsAppDriver))]
    public void Create_ReturnsCorrectDriverType(string platform, Type expectedType)
    {
        using var driver = AppDriverFactory.Create(platform);
        Assert.IsType(expectedType, driver);
    }

    [Fact]
    public void Create_UnknownPlatform_Throws()
    {
        Assert.Throws<ArgumentException>(() => AppDriverFactory.Create("unknown"));
    }
}

public class MacCatalystAppDriverTests
{
    [Fact]
    public void Platform_ReturnsMacCatalyst()
    {
        using var driver = new MacCatalystAppDriver();
        Assert.Equal("MacCatalyst", driver.Platform);
    }

    [Fact]
    public async Task ConnectAsync_NoAgent_ThrowsInvalidOperation()
    {
        using var driver = new MacCatalystAppDriver();
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ConnectAsync("localhost", 19999));
    }

    [Fact]
    public async Task GetTree_BeforeConnect_ThrowsInvalidOperation()
    {
        using var driver = new MacCatalystAppDriver();
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.GetTreeAsync());
    }
}

public class AndroidAppDriverTests
{
    [Fact]
    public void Platform_ReturnsAndroid()
    {
        using var driver = new AndroidAppDriver();
        Assert.Equal("Android", driver.Platform);
    }
}

public class iOSSimulatorAppDriverTests
{
    [Fact]
    public void Platform_ReturnsiOSSimulator()
    {
        using var driver = new iOSSimulatorAppDriver();
        Assert.Equal("iOSSimulator", driver.Platform);
    }
}

public class WindowsAppDriverTests
{
    [Fact]
    public void Platform_ReturnsWindows()
    {
        using var driver = new WindowsAppDriver();
        Assert.Equal("Windows", driver.Platform);
    }
}

public class ElementInfoTests
{
    [Fact]
    public void DefaultValues()
    {
        var info = new ElementInfo();
        Assert.Equal(string.Empty, info.Id);
        Assert.Null(info.ParentId);
        Assert.Equal(string.Empty, info.Type);
        Assert.Null(info.AutomationId);
        Assert.Null(info.Text);
        Assert.False(info.IsVisible);
        Assert.False(info.IsEnabled);
        Assert.False(info.IsFocused);
        Assert.Null(info.Bounds);
        Assert.Null(info.Children);
    }

    [Fact]
    public void Serialization_RoundTrips()
    {
        var info = new ElementInfo
        {
            Id = "btn1",
            Type = "Button",
            FullType = "Microsoft.Maui.Controls.Button",
            AutomationId = "SubmitBtn",
            Text = "Submit",
            IsVisible = true,
            IsEnabled = true,
            Bounds = new BoundsInfo { X = 10, Y = 20, Width = 100, Height = 44 }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(info);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ElementInfo>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("btn1", deserialized.Id);
        Assert.Equal("Button", deserialized.Type);
        Assert.Equal("SubmitBtn", deserialized.AutomationId);
        Assert.Equal("Submit", deserialized.Text);
        Assert.True(deserialized.IsVisible);
        Assert.NotNull(deserialized.Bounds);
        Assert.Equal(100, deserialized.Bounds.Width);
    }
}

public class AgentStatusTests
{
    [Fact]
    public void Deserialization_Works()
    {
        var json = """{"agent":"MauiDevFlow.Agent","version":"1.0.0","platform":"MacCatalyst","deviceType":"Virtual","idiom":"Desktop","appName":"SampleMauiApp","running":true}""";
        var status = System.Text.Json.JsonSerializer.Deserialize<AgentStatus>(json);

        Assert.NotNull(status);
        Assert.Equal("MauiDevFlow.Agent", status.Agent);
        Assert.Equal("1.0.0", status.Version);
        Assert.Equal("MacCatalyst", status.Platform);
        Assert.True(status.Running);
    }
}
