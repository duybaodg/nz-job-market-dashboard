using System.Net;
using System.Text;
using Application.Jobs;
using FluentAssertions;
using Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace Application.Tests;

public sealed class OpenAiFieldEnrichmentClientTests
{
    [Fact]
    public async Task Parses_a_strict_json_answer_and_sends_the_expected_request()
    {
        const string content = """
            {"salaryMin":120000,"salaryMax":150000,"currency":"NZD","period":"Year",
             "workMode":"Hybrid","seniority":"Senior","confidence":0.82}
            """;
        var handler = new RecordingHandler(HttpStatusCode.OK, ChatCompletion(content));
        var client = CreateClient(handler);

        var result = await client.ExtractAsync(
            new AiFieldEnrichmentRequest("Senior Engineer", "Auckland", "Hybrid role.", null),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.SalaryMin.Should().Be(120_000m);
        result.SalaryMax.Should().Be(150_000m);
        result.Period.Should().Be("Year");
        result.WorkMode.Should().Be("Hybrid");
        result.Seniority.Should().Be("Senior");
        result.Confidence.Should().Be(0.82m);
        result.Model.Should().Be("test-model");

        handler.RequestUri.Should().Be("https://example.test/v1/chat/completions");
        handler.Authorization.Should().Be("Bearer test-key");
        handler.RequestBody.Should().Contain("json_object");
        handler.RequestBody.Should().Contain("Senior Engineer");
    }

    [Fact]
    public async Task Parses_json_wrapped_in_markdown_fences()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            ChatCompletion("```json\n{\"workMode\":\"Remote\",\"confidence\":0.9}\n```"));
        var client = CreateClient(handler);

        var result = await client.ExtractAsync(
            new AiFieldEnrichmentRequest("Engineer", null, "Remote role.", null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.WorkMode.Should().Be("Remote");
        result.SalaryMin.Should().BeNull();
    }

    [Fact]
    public async Task A_non_success_status_returns_null()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests, "{}");
        var client = CreateClient(handler);

        var result = await client.ExtractAsync(
            new AiFieldEnrichmentRequest("Engineer", null, "Role.", null), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Unparsable_content_returns_null_rather_than_throwing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, ChatCompletion("not json at all"));
        var client = CreateClient(handler);

        var result = await client.ExtractAsync(
            new AiFieldEnrichmentRequest("Engineer", null, "Role.", null), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public void String_and_alternate_case_numbers_are_read_defensively()
    {
        var result = OpenAiFieldEnrichmentClient.ParseResponse(
            ChatCompletion("{\"SalaryMin\":\"120000\",\"workmode\":\"On-site\",\"confidence\":\"0.7\"}"),
            "test-model");

        result.Should().NotBeNull();
        result!.SalaryMin.Should().Be(120_000m);
        result.WorkMode.Should().Be("On-site");
        result.Confidence.Should().Be(0.7m);
    }

    private static OpenAiFieldEnrichmentClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var options = Options.Create(new AiFieldEnrichmentOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            BaseUrl = "https://example.test/v1",
            Model = "test-model"
        });
        return new OpenAiFieldEnrichmentClient(httpClient, options);
    }

    private static string ChatCompletion(string content)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(content);
        return $"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":{escaped}}}}}]}}";
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
