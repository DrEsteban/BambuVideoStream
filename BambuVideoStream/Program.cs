using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using BambuVideoStream;
using BambuVideoStream.Models;
using BambuVideoStream.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

#if UseVelopack
await VelopackSupport.InitializeAsync(args);
#endif

// Extract embedded resources to the file system
{
    Directory.CreateDirectory(Constants.OBS.ImageDir);
    const string imagePrefix = "BambuVideoStream.Images.";
    var images = Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(r => r.StartsWith(imagePrefix));
    foreach (var image in images)
    {
        var fileName = image[imagePrefix.Length..];
        var filePath = Path.Combine(Constants.OBS.ImageDir, fileName);
        if (!File.Exists(filePath))
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(image);
            using var file = File.Create(filePath);
            resource.CopyTo(file);
        }
    }
}

// Build the host
var builder = new HostApplicationBuilder(args);
var services = builder.Services;
var configuration = builder.Configuration;
var environment = builder.Environment;
var logging = builder.Logging;

// Config files with connection settings and user secrets
configuration.AddJsonFile("secrets.json", optional: true);
configuration.AddJsonFile("connection.json", optional: true);
#if UseVelopack
configuration.AddJsonFile(VelopackSupport.ConnectionSettingsFilePath, optional: false);
#endif

// Telemetry
services.AddMetrics();
bool useAzureMonitor = !string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
bool useOtlpExporter = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
logging.AddOpenTelemetry(o =>
{
    o.ParseStateValues =
        o.IncludeFormattedMessage =
        o.IncludeScopes = true;
});
var otel = services.AddOpenTelemetry()
    .ConfigureResource(r =>
    {
        _ = r.AddService(
                environment.ApplicationName,
                serviceNamespace: "DrEsteban",
                serviceVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString())
            .AddAttributes([KeyValuePair.Create<string, object>("DOTNET_ENVIRONMENT", environment.EnvironmentName)])
            .AddEnvironmentVariableDetector()
            .AddHostDetector()
            .AddProcessDetector()
            .AddProcessRuntimeDetector()
            .AddOperatingSystemDetector()
            .AddTelemetrySdk();
    })
    .WithLogging(l =>
    {
        if (useAzureMonitor)
        {
            l.AddAzureMonitorLogExporter();
        }
    })
    .WithTracing(t =>
    {
        if (useAzureMonitor)
        {
            t.AddAzureMonitorTraceExporter();
        }
    })
    .WithMetrics(m =>
    {
        m.AddRuntimeInstrumentation()
            .AddProcessInstrumentation();
        if (useAzureMonitor)
        {
            m.AddAzureMonitorMetricExporter();
        }
    });
if (useOtlpExporter)
{
    otel.UseOtlpExporter();
}

// Log files
string fileLogFormat = configuration.GetValue<string>("Logging:File:FilenameFormat");
if (!string.IsNullOrEmpty(fileLogFormat))
{
    if (!Enum.TryParse(configuration.GetValue<string>("Logging:File:MinimumLevel"), out LogLevel minLevel))
    {
        minLevel = LogLevel.Information;
    }
    builder.Logging.AddFile(fileLogFormat, minimumLevel: minLevel, isJson: false);
    builder.Logging.AddFile(Path.ChangeExtension(fileLogFormat, ".json"), minimumLevel: minLevel, isJson: true);
}

// Services
services.AddOptionsWithValidateOnStart<BambuSettings>()
    .BindConfiguration(nameof(BambuSettings))
    .ValidateDataAnnotations()
    .Configure((BambuSettings settings, ILogger<Program> logger) =>
    {
        if (string.IsNullOrWhiteSpace(settings.PathToSDP))
        {
            try
            {
                settings.PathToSDP = Path.GetFullPath(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BambuStudio/cameratools/ffmpeg.sdp"));
            }
            catch (PlatformNotSupportedException)
            {
                // Doesn't work on Mac/Linux, user must set via appsettings
                logger.LogTrace("Platform '{platform}' does not support SpecialFolder.ApplicationData. User must set PathToSDP via appsettings.", Environment.OSVersion.Platform);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error setting default path to SDP. That's okay if you've set it via appsettings.");
            }
        }
    });
services.AddOptionsWithValidateOnStart<OBSSettings>()
    .BindConfiguration(nameof(OBSSettings))
    .ValidateDataAnnotations();
services.AddOptions<AppSettings>()
    .BindConfiguration(nameof(AppSettings));
services.AddTransient<FtpService>();
services.AddTransient<MyOBSWebsocket>();
services.AddHostedService<BambuStreamBackgroundService>();

// Build and run
using var host = builder.Build();
await host.RunAsync();

#if UseVelopack
Console.WriteLine("Press any key to exit...");
Console.ReadKey(intercept: true);
#endif
