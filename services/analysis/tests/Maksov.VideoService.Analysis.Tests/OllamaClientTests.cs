using System.Net;
using System.Text;
using Maksov.VideoService.Analysis.Worker;
using Maksov.VideoService.Analysis.Worker.Adapters.Ollama;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Analysis.Tests;

public sealed class OllamaClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static OllamaClient Client(StubHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://ollama:11434") },
        Options.Create(new AnalysisOptions { Captioner = new CaptionerOptions { Model = "moondream", Prompt = "describe" } }));

    [Fact]
    public async Task Sends_image_as_base64_and_returns_trimmed_caption()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"response\":\"  A person walks down the street.  \",\"done\":true}");

        var caption = await Client(handler).DescribeAsync([1, 2, 3], TestContext.Current.CancellationToken);

        Assert.Equal("A person walks down the street.", caption);
        Assert.Equal("http://ollama:11434/api/generate", handler.RequestUri!.ToString());
        Assert.Contains("\"model\":\"moondream\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"images\":[\"AQID\"]", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_response_is_null()
    {
        var caption = await Client(new StubHandler(HttpStatusCode.OK, "{\"response\":\"   \"}")).DescribeAsync([1], TestContext.Current.CancellationToken);

        Assert.Null(caption);
    }

    [Fact]
    public async Task Server_error_is_thrown_for_the_captioner_to_handle()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(new StubHandler(HttpStatusCode.InternalServerError, "{}")).DescribeAsync([1], TestContext.Current.CancellationToken));
    }
}
