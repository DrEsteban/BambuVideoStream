using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BambuVideoStream;
using BambuVideoStream.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#if UseVelopack
using Velopack;
using Velopack.Sources;

static async Task UpdateCheckAsync(string[] args)
{
    VelopackApp.Build().Run();

    var mgr = new UpdateManager(new GithubSource("https://github.com/DrEsteban/BambuVideoStream", null, false), new UpdateOptions
    {
        AllowVersionDowngrade = true,
    });

    // check for new version
    if (mgr.IsInstalled)
    {
        Console.WriteLine("Checking for updates...");
        try
        {
            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion != null)
            {
                Console.WriteLine($"New update found! ({mgr.CurrentVersion} -> {newVersion.TargetFullRelease.Version})");
                Console.WriteLine("Press 'y' within 10 seconds to update...");
                var sw = Stopwatch.StartNew();
                while (!Console.KeyAvailable && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(250);
                }
                if (!Console.KeyAvailable || Console.ReadKey().Key != ConsoleKey.Y)
                {
                    Console.WriteLine("Update skipped.");
                    return;
                }
    
                Console.WriteLine("Updating...");
                // download new version
                await mgr.DownloadUpdatesAsync(newVersion, progress => Console.WriteLine($"{progress}% completed", progress));
                Console.WriteLine("Download completed. Restarting...");
    
                // install new version and restart app
                mgr.ApplyUpdatesAndRestart(newVersion, args);
            }
        }
        catch (Exception e)
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error checking for updates: {e.GetType().Name}: {e.Message}");
            Console.ForegroundColor = c;
        }
    }
}

await UpdateCheckAsync(args);
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

var builder = new HostApplicationBuilder(args);
// Optional config file that can contain user settings
builder.Configuration.AddJsonFile("secrets.json", optional: true);

string fileLogFormat = builder.Configuration.GetValue<string>("Logging:File:FilenameFormat");
if (!string.IsNullOrEmpty(fileLogFormat))
{
    if (!Enum.TryParse(builder.Configuration.GetValue<string>("Logging:File:MinimumLevel"), out LogLevel minLevel))
    {
        minLevel = LogLevel.Information;
    }
    builder.Logging.AddFile(fileLogFormat, minimumLevel: minLevel, isJson: false);
    builder.Logging.AddFile(Path.ChangeExtension(fileLogFormat, ".json"), minimumLevel: minLevel, isJson: true);
}
builder.Services.Configure<BambuSettings>(builder.Configuration.GetSection(nameof(BambuSettings)));
builder.Services.Configure<OBSSettings>(builder.Configuration.GetSection(nameof(OBSSettings)));
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection(nameof(AppSettings)));
builder.Services.AddTransient<FtpService>();
builder.Services.AddTransient<MyOBSWebsocket>();
builder.Services.AddHostedService<BambuStreamBackgroundService>();

var host = builder.Build();
GlobalLogger = host.Services.GetRequiredService<ILogger<Program>>();

await host.RunAsync();

public partial class Program
{
    internal static ILogger<Program> GlobalLogger { get; private set; }
}
