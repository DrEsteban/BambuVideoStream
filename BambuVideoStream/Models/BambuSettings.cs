using System;
using System.IO;

namespace BambuVideoStream.Models;

public class BambuSettings
{
    public string IpAddress { get; set; }
    public int MqttPort { get; set; }
    public int FtpPort { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string Serial { get; set; }
    public string PathToSDP { get; set; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BambuStudio/cameratools/ffmpeg.sdp");
}
