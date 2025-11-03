using System.ComponentModel.DataAnnotations;

namespace BambuVideoStream.Models;

public class BambuSettings
{
    [Required]
    public required string IpAddress { get; set; }

    [Range(1, ushort.MaxValue)]
    public int MqttPort { get; set; }

    [Range(1, ushort.MaxValue)]
    public int FtpPort { get; set; }

    [Required]
    public required string Username { get; set; }

    [Required]
    public required string Password { get; set; }

    [Required]
    public required string Serial { get; set; }

    public string? PathToSDP { get; set; }
}
