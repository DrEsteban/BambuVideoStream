using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BambuVideoStream;
using BambuVideoStream.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

var foo = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BambuStudio/cameratools/ffmpeg.sdp");
var x = Process.GetProcessesByName("obs64");
var y = Process.GetProcessesByName("bambu-studio");
var z = Process.GetProcessesByName("bambu_source");

var sourceInfo = new ProcessStartInfo
{
    FileName = "C:\\Users\\steve\\AppData\\Roaming\\BambuStudio\\cameratools\\bambu_source.exe",
    Arguments = "bambu:///camera/C:/Users/steve/AppData/Roaming/BambuStudio/cameratools/url.txt",
    //WindowStyle = ProcessWindowStyle.Normal,
    RedirectStandardError = true,
    RedirectStandardOutput = true,
    RedirectStandardInput = true,
    UseShellExecute = false,
};
var ffmpegInfo = new ProcessStartInfo
{
    FileName = "C:\\Users\\steve\\AppData\\Roaming\\BambuStudio\\cameratools\\ffmpeg.exe",
    Arguments = "-fflags nobuffer -flags low_delay -analyzeduration 10 -probesize 3200 -f h264 -i pipe: -vcodec copy -f rtp rtp://127.0.0.1:1234",
    RedirectStandardError = true,
    RedirectStandardOutput = true,
    RedirectStandardInput = true,
    UseShellExecute = false,
};

var bambuSource = new Process
{
    StartInfo = sourceInfo,
    //EnableRaisingEvents = true
};
var ffmpegProc = new Process
{
    StartInfo = ffmpegInfo,
    //EnableRaisingEvents = true
};
//p.StandardError.BaseStream
//p.ErrorDataReceived += (sender, args) => Console.WriteLine("Error: " + args.Data);
bambuSource.Start();
//bambuSource.BeginOutputReadLine();
//bambuSource.BeginErrorReadLine();

//ffmpegProc.ErrorDataReceived += (sender, args) => Console.WriteLine("Error: " + args.Data);
//ffmpegProc.OutputDataReceived += (sender, args) => Console.WriteLine("Output: " + args.Data);
ffmpegProc.Start();
//ffmpegProc.StandardInput.AutoFlush = true;
//ffmpegProc.BeginErrorReadLine();
//ffmpegProc.BeginOutputReadLine();

Task.Run(() =>
{
    try
    {
        while (true)
        {
            ffmpegProc.StandardInput.BaseStream.WriteByte((byte)bambuSource.StandardOutput.BaseStream.ReadByte());
            ffmpegProc.StandardInput.BaseStream.Flush();
        }
        //bambuSource.StandardOutput.BaseStream.CopyTo(ffmpegProc.StandardInput.BaseStream);
    }
    catch (Exception e)
    {
        Console.WriteLine(e);
    }
    //bambuSource.StandardOutput.BaseStream.CopyTo(ffmpegProc.StandardInput.BaseStream);
    //while (true)
    //{
    //    ffmpegProc.StandardInput.Write(bambuSource.StandardOutput.Read());
    //}
});

//var t = new Thread(() => bambuSource.StandardOutput.BaseStream.CopyTo(ffmpegProc.StandardInput.BaseStream));
//t.Start();
//Task.Run(() => p.StandardOutput.BaseStream.CopyToAsync(p2.StandardInput.BaseStream));


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

await host.RunAsync();