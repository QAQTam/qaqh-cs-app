using Qaqh.Sdk;
using Xunit;

namespace Qaqh.Sdk.Tests;

public class ErrorWireTests
{
    /// <summary>daemon 的 502 rejected-ack 体必须解析出 code+message（而非退化成 Http）。</summary>
    [Fact]
    public void RejectedAckBodySurfacesCodeAndMessage()
    {
        const string body =
            "{\"command_id\":\"019x\",\"status\":\"rejected\",\"code\":\"operation_failed\",\"message\":\"请先在电脑端设置默认服务商和模型\"}";
        var ex = SdkExceptionTestsHelper.Parse(body);
        Assert.Equal(SdkErrorKind.Api, ex.Kind);
        Assert.Equal("operation_failed", ex.Detail);
        Assert.Equal("请先在电脑端设置默认服务商和模型", ex.Message);
    }
}

public static class SdkExceptionTestsHelper
{
    /// <summary>通过可序列化响应复现 FromResponseAsync 的解析路径。</summary>
    public static SdkException Parse(string body)
    {
        var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        return SdkException
            .FromResponseAsync(response, "/ringing/v2/commands")
            .GetAwaiter()
            .GetResult();
    }
}
