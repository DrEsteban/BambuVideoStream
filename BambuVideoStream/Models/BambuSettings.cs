using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace BambuVideoStream.Models;

public class BambuSettings
{
    public BambuSettings()
    {
        try
        {
            this.PathToSDP = Path.GetFullPath(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BambuStudio/cameratools/ffmpeg.sdp"));
        }
        catch (PlatformNotSupportedException)
        {
            // Doesn't work on Mac/Linux, user must set via appsettings
            Program.GlobalLogger.LogTrace("Platform '{platform}' does not support SpecialFolder.ApplicationData. User must set PathToSDP via appsettings.", Environment.OSVersion.Platform);
        }
        catch (Exception e)
        {
            Program.GlobalLogger.LogError(e, "Error setting default path to SDP. That's okay if you've set it via appsettings.");
        }
    }

    public string IpAddress { get; set; }
    public int MqttPort { get; set; }
    public int FtpPort { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string Serial { get; set; }
    public string PathToSDP { get; set; }
}
