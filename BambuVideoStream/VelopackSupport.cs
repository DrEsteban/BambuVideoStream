#if UseVelopack

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace BambuVideoStream;

/// <summary>
/// Velopack installation and update support
/// </summary>
internal static class VelopackSupport
{
    public static async Task UpdateCheckAsync(string[] args)
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
}

#endif