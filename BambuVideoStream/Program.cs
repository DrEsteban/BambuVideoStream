﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BambuVideoStream;
using BambuVideoStream.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

var builder = new HostApplicationBuilder(args);
// Config files with connection settings and user secrets
builder.Configuration.AddJsonFile("secrets.json", optional: true);
builder.Configuration.AddJsonFile("connection.json", optional: true);
#if UseVelopack
builder.Configuration.AddJsonFile(VelopackSupport.ConnectionSettingsFilePath, optional: false);
#endif

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

using var host = builder.Build();
GlobalLogger = host.Services.GetRequiredService<ILogger<Program>>();

await host.RunAsync();

Console.WriteLine("Press any key to exit...");
Console.ReadKey(intercept: true);
// End


// Program.cs class members
public partial class Program
{
    internal static ILogger<Program> GlobalLogger { get; private set; }
}
