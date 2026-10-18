using System.Net;
using System.Net.Http.Json;
using Blackbox.Dock;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsDockServerTests
{
    [Fact]
    public async Task Server_requires_the_capability_url_and_security_header()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            await using var server = new ObsDockServer(
                store,
                NullLogger<ObsDockServer>.Instance);
            var target = new RecordingTarget();
            var endpoint = await server.StartAsync(target);
            using var client = new HttpClient();

            using var validResponse = await client.GetAsync(endpoint.Url);
            var invalidUrl = new Uri(endpoint.Url.AbsoluteUri.Replace(
                store.LoadOrCreate().Token,
                new string('0', 64),
                StringComparison.Ordinal));
            using var invalidResponse = await client.GetAsync(invalidUrl);
            using var forgedHostRequest = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
            forgedHostRequest.Headers.Host = "localhost";
            using var forgedHostResponse = await client.SendAsync(forgedHostRequest);

            Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, invalidResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, forgedHostResponse.StatusCode);
            Assert.Contains("default-src 'none'", validResponse.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-referrer", validResponse.Headers.GetValues("Referrer-Policy").Single());
            Assert.False(validResponse.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Valid_json_command_reaches_the_control_target()
    {
        var root = CreateRoot();
        try
        {
            await using var server = new ObsDockServer(
                CreateStore(root),
                NullLogger<ObsDockServer>.Instance);
            var target = new RecordingTarget();
            var endpoint = await server.StartAsync(target);
            using var client = new HttpClient();
            using var rejectedResponse = await client.PostAsJsonAsync(
                new Uri(endpoint.Url, "api/command"),
                new { command = "AddMarker" });
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(endpoint.Url, "api/command"))
            {
                Content = JsonContent.Create(new { command = "AddMarker" })
            };
            request.Headers.Add("X-Blackbox-Dock", "1");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(ObsDockCommand.AddMarker, Assert.Single(target.Commands));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ObsDockSettingsStore CreateStore(string root) =>
        new(
            new ObsDockOptions { SettingsPath = Path.Combine(root, "obs-dock.json") },
            NullLogger<ObsDockSettingsStore>.Instance);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingTarget : IObsDockControlTarget
    {
        public List<ObsDockCommand> Commands { get; } = [];

        public Task<ObsDockState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ObsDockState(
                true,
                10,
                false,
                true,
                false,
                "Test Game",
                "Recording",
                false,
                true,
                true,
                true,
                true));

        public Task ExecuteAsync(
            ObsDockCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
