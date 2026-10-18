using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blackbox.Dock;

public sealed class ObsDockServer(
    ObsDockSettingsStore settingsStore,
    ILogger<ObsDockServer> logger) : IAsyncDisposable
{
    private const int MaximumCommandBytes = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private WebApplication? _application;
    private ObsDockEndpoint? _endpoint;

    public ObsDockEndpoint? Endpoint => _endpoint;

    public async Task<ObsDockEndpoint> StartAsync(
        IObsDockControlTarget controlTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlTarget);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_endpoint is not null)
            {
                return _endpoint;
            }

            var settings = settingsStore.LoadOrCreate();
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var port = attempt == 0 && IsPortAvailable(settings.Port)
                    ? settings.Port
                    : GetAvailablePort();
                var application = BuildApplication(port, settings.Token, controlTarget);
                try
                {
                    await application.StartAsync(cancellationToken);
                    settings = settingsStore.UpdatePort(port);
                    _application = application;
                    _endpoint = new ObsDockEndpoint(settingsStore.GetDockUri(), port);
                    logger.LogInformation(
                        "Started the authenticated OBS dock service on 127.0.0.1:{Port}.",
                        port);
                    return _endpoint;
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    lastFailure = ex;
                    await application.DisposeAsync();
                }
            }

            throw new InvalidOperationException(
                "Blackbox could not reserve a local port for the OBS dock.",
                lastFailure);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_application is null)
            {
                return;
            }

            var application = _application;
            _application = null;
            _endpoint = null;
            await application.StopAsync(cancellationToken);
            await application.DisposeAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private WebApplication BuildApplication(
        int port,
        string token,
        IObsDockControlTarget controlTarget)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ObsDockServer).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.AddServerHeader = false;
            serverOptions.Limits.MaxRequestBodySize = MaximumCommandBytes;
            serverOptions.Listen(IPAddress.Loopback, port);
        });

        var application = builder.Build();
        var root = $"/dock/{token}/";
        application.Use(async (context, next) =>
        {
            AddSecurityHeaders(context.Response.Headers);
            if (!context.Request.Host.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                !HasValidToken(context.Request.Path, token))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

        MapAsset(application, root, "dock.html", "text/html; charset=utf-8");
        MapAsset(application, root + "dock.css", "dock.css", "text/css; charset=utf-8");
        MapAsset(application, root + "dock.js", "dock.js", "text/javascript; charset=utf-8");
        MapAsset(application, root + "blackbox.png", "blackbox.png", "image/png");

        application.MapGet(root + "api/state", async context =>
        {
            try
            {
                var state = await controlTarget.GetStateAsync(context.RequestAborted);
                await WriteJsonAsync(context, state, StatusCodes.Status200OK);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The OBS dock could not read Blackbox state.");
                await WriteJsonAsync(
                    context,
                    new DockError("Blackbox state is temporarily unavailable."),
                    StatusCodes.Status503ServiceUnavailable);
            }
        });

        application.MapPost(root + "api/command", async context =>
        {
            if (context.Request.ContentType?.StartsWith(
                    "application/json",
                    StringComparison.OrdinalIgnoreCase) != true ||
                !context.Request.Headers.TryGetValue("X-Blackbox-Dock", out var header) ||
                header.Count != 1 ||
                !string.Equals(header[0], "1", StringComparison.Ordinal))
            {
                await WriteJsonAsync(
                    context,
                    new DockError("The command request was rejected."),
                    StatusCodes.Status400BadRequest);
                return;
            }

            DockCommandRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<DockCommandRequest>(
                    context.Request.Body,
                    JsonOptions,
                    context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request is null ||
                !Enum.TryParse<ObsDockCommand>(request.Command, true, out var command) ||
                !Enum.IsDefined(command))
            {
                await WriteJsonAsync(
                    context,
                    new DockError("The dock command is not recognized."),
                    StatusCodes.Status400BadRequest);
                return;
            }

            await _commandGate.WaitAsync(context.RequestAborted);
            try
            {
                await controlTarget.ExecuteAsync(command, context.RequestAborted);
                var state = await controlTarget.GetStateAsync(context.RequestAborted);
                await WriteJsonAsync(context, state, StatusCodes.Status200OK);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OBS dock command {DockCommand} failed.", command);
                await WriteJsonAsync(
                    context,
                    new DockError("The command failed. Check Blackbox for details."),
                    StatusCodes.Status409Conflict);
            }
            finally
            {
                _commandGate.Release();
            }
        });

        return application;
    }

    private static void MapAsset(
        WebApplication application,
        string route,
        string assetName,
        string contentType)
    {
        var content = DockAssets.Read(assetName);
        application.MapGet(route, async context =>
        {
            context.Response.ContentType = contentType;
            context.Response.ContentLength = content.Length;
            await context.Response.Body.WriteAsync(content, context.RequestAborted);
        });
    }

    private static bool HasValidToken(PathString path, string expectedToken)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].Equals("dock", StringComparison.Ordinal))
        {
            return false;
        }

        var supplied = Encoding.ASCII.GetBytes(segments[1]);
        var expected = Encoding.ASCII.GetBytes(expectedToken);
        return supplied.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static void AddSecurityHeaders(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        headers.CacheControl = "no-store, max-age=0";
        headers.Pragma = "no-cache";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), geolocation=(), microphone=()";
    }

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        T value,
        int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            JsonOptions,
            context.RequestAborted);
    }

    private static bool IsPortAvailable(int port)
    {
        if (port is < 1024 or > 65535)
        {
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _commandGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private sealed record DockCommandRequest(string Command);

    private sealed record DockError(string Error);
}
