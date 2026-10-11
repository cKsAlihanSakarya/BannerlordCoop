using Coop.Core.Server.Services.Admin;
using Xunit;

namespace Coop.Tests.Server.Services.Admin;

public class ServerAdminProtocolTests
{
    [Theory]
    [InlineData("status", "status", null)]
    [InlineData("  STATUS \r", "status", null)]
    [InlineData("cancel", "cancel", null)]
    [InlineData("exit", "exit", null)]
    [InlineData("unstuck 76561198000000001", "unstuck", "76561198000000001")]
    [InlineData("unstuck   Derthert the Bold ", "unstuck", "Derthert the Bold")]
    [InlineData("shutdown 60", "shutdown", "60")]
    public void TryParse_AcceptsKnownRequests(string line, string op, string argument)
    {
        Assert.True(ServerAdminProtocol.TryParse(line, out var request, out var error));
        Assert.Null(error);
        Assert.Equal(op, request.Op);
        Assert.Equal(argument, request.Argument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("reboot")]
    [InlineData("status now")]
    [InlineData("exit 5")]
    [InlineData("unstuck")]
    [InlineData("shutdown")]
    [InlineData("shutdown soon")]
    [InlineData("shutdown -5")]
    public void TryParse_RefusesMalformedRequests(string line)
    {
        Assert.False(ServerAdminProtocol.TryParse(line, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryParse_RefusesOverlongRequests()
    {
        string line = "unstuck " + new string('x', ServerAdminProtocol.MaxRequestLength);

        Assert.False(ServerAdminProtocol.TryParse(line, out _, out _));
    }

    [Fact]
    public void GetPipeName_IncludesThePort()
    {
        Assert.Equal("BannerlordCoop.ServerAdmin.4200", ServerAdminProtocol.GetPipeName(4200));
    }

    [Fact]
    public void JsonObjectWriter_EscapesSoAReplyStaysOneLine()
    {
        string json = new JsonObjectWriter()
            .String("text", "a \"quoted\" \\ path\nnext\u0001\u2028")
            .String("missing", null)
            .Bool("ok", true)
            .Number("n", 42)
            .Array("items", new[] { new JsonObjectWriter().Number("x", 1), new JsonObjectWriter() })
            .ToString();

        Assert.Equal(
            "{\"text\":\"a \\\"quoted\\\" \\\\ path\\nnext\\u0001\\u2028\",\"missing\":null,\"ok\":true,\"n\":42," +
            "\"items\":[{\"x\":1},{}]}",
            json);
        Assert.DoesNotContain('\n', json);
    }
}
