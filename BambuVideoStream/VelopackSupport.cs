#if UseVelopack

using System.Diagnostics;
using BambuVideoStream.Models;
using NuGet.Versioning;
using Velopack;
using Velopack.Sources;

namespace BambuVideoStream;

/// <summary>
/// Velopack installation and update support
/// </summary>
internal static class VelopackSupport
{
    public const string AppId = "BambuVideoStream";

    public static string SettingsDirectory
    {
        get
        {
            string localSettingsDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            localSettingsDir = Path.Combine(localSettingsDir, AppId);
            Directory.CreateDirectory(localSettingsDir);
            return localSettingsDir;
        }
    }

    public static string ConnectionSettingsFilePath
        => Path.Combine(SettingsDirectory, "connection.json");

    public static async Task InitializeAsync(string[] args)
    {
        VelopackApp.Build().Run();

        var mgr = new UpdateManager(new GithubSource("https://github.com/DrEsteban/BambuVideoStream", null, false), new UpdateOptions
        {
            AllowVersionDowngrade = true,
        });

        if (!mgr.IsInstalled)
        {
            return;
        }

        // header
        Console.Clear();
        Console.WriteLine("*******************************");
        Console.WriteLine("*   Bambu Video Stream Tool   *");
        Console.WriteLine("*******************************");
        Console.WriteLine();

        // check for connection info file
        string connectionSettingsFilePath = ConnectionSettingsFilePath;
        if (!File.Exists(connectionSettingsFilePath))
        {
            string template = """
                {
                  "BambuSettings": {
                    "ipAddress": "<ip of printer>",
                    "MqttPort": 8883,
                    "FtpPort": 990,
                    "username": "bblp",
                    "password": "<password of printer>",
                    "serial": "<serial of printer>"
                  },
                  "ObsSettings": {
                    "WsAddress": "ws://localhost:4455",
                    "WsPassword": "<your password here, or comment out>"
                  }
                }
                """;
            File.WriteAllText(connectionSettingsFilePath, template);
            Console.WriteLine("You must configure a connection.json file to run this application.");
            Console.WriteLine();
            Console.WriteLine("We will now launch the text editor to create this file at:");
            Console.WriteLine($"  '{connectionSettingsFilePath}'");
            Console.WriteLine("When you're done filling it out: Save the file, close your text editor, and run the application again.");
            Console.WriteLine();
            Console.WriteLine("Press ENTER to continue, or Ctrl+C to quit...");
            Console.ReadLine();
            LaunchDefaultTextEditor(connectionSettingsFilePath);
            Exit();
        }

        // main menu
        var wait = TimeSpan.FromSeconds(15);
        void printMenu()
        {
            Console.WriteLine("What would you like to do?");
            Console.WriteLine("  1. Run the application");
            Console.WriteLine("  2. Edit the connection settings");
            Console.WriteLine("  3. Edit the application settings");
            Console.WriteLine("  4. Check for updates");
            Console.WriteLine();
            Console.WriteLine($"Will default to '1' in {(int)wait.TotalSeconds} seconds. Press 'q' or Ctrl+C to quit...");
        }

        printMenu();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < wait)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.D1:
                    case ConsoleKey.NumPad1:
                    case ConsoleKey.Enter:
                        // Return to launch the application
                        Console.Clear();
                        return;

                    case ConsoleKey.D2:
                    case ConsoleKey.NumPad2:
                        LaunchDefaultTextEditor(connectionSettingsFilePath);
                        Exit();
                        break;

                    case ConsoleKey.D3:
                    case ConsoleKey.NumPad3:
                        string installDir = AppContext.BaseDirectory;
                        string appSettingsFilePath = Path.Combine(installDir, "appsettings.json");
                        LaunchDefaultTextEditor(appSettingsFilePath);
                        Exit();
                        break;

                    case ConsoleKey.D4:
                    case ConsoleKey.NumPad4:
                        if (!await CheckForUpdatesAsync(mgr, args))
                        {
                            Console.WriteLine();
                            Console.WriteLine("Choose another option:");
                            Console.WriteLine();
                            printMenu();
                            timer.Restart();
                            continue;
                        }
                        break;

                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        Exit();
                        break;
                }
            }

            await Task.Delay(200);
        }
    }

    internal static void LaunchDefaultTextEditor(string file)
    {
        using var _ = Process.Start(new ProcessStartInfo(file)
        {
            UseShellExecute = true,
        });
    }

    internal static async Task<bool> CheckForUpdatesAsync(UpdateManager mgr, string[] args)
    {
        // check for new version
        Console.WriteLine("Checking for updates...");
        try
        {
            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion != null && newVersion.TargetFullRelease.Version > (mgr.CurrentVersion ?? new SemanticVersion(0, 0, 0)))
            {
                Console.WriteLine();
                Console.WriteLine($"New update found! ({mgr.CurrentVersion?.ToString() ?? "<Unknown>"} -> {newVersion.TargetFullRelease.Version})");
                Console.WriteLine("NOTE: When you update, your connection settings will be preserved but application settings will be reset.");
                Console.WriteLine();
                Console.WriteLine("Press 'y' to update, or any other key to skip...");
                var key = Console.ReadKey().Key;
                Console.WriteLine();
                if (key != ConsoleKey.Y)
                {
                    Console.WriteLine("Update skipped.");
                }
                else
                {
                    Console.WriteLine("Updating...");
                    // download new version
                    await mgr.DownloadUpdatesAsync(newVersion, progress => Console.WriteLine($"{progress}% completed", progress));
                    Console.WriteLine("Download completed. Restarting...");

                    // install new version and restart app
                    mgr.ApplyUpdatesAndRestart(newVersion, args);
                    throw new UnreachableException($"{nameof(mgr.ApplyUpdatesAndRestart)} should have exited the application!");
                }
            }
            else
            {
                Console.WriteLine("No updates available.");
            }
        }
        catch (Exception e)
        {
            PrintError($"Error checking for updates: {e.GetType().Name}: {e.Message}");
            await Task.Delay(TimeSpan.FromSeconds(4));
        }

        return false;
    }

    internal static void Exit()
    {
        Environment.Exit(0);
    }

    internal static void PrintError(string err)
    {
        var c = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(err);
        Console.ForegroundColor = c;
    }
}

#endif