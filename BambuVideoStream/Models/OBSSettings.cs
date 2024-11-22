using System.ComponentModel.DataAnnotations;

namespace BambuVideoStream.Models;

public class OBSSettings
{
    [Required]
    public string WsAddress { get; set; }

    public string WsPassword { get; set; }

    [Required]
    public string BambuScene { get; set; } = "BambuScene";

    [Required]
    public string BambuStreamSource { get; set; } = "BambuStreamSource";

    public bool StartStreamOnStartup { get; set; }

    public bool StopStreamOnPrinterIdle { get; set; }

    public bool ForceCreateInputs { get; set; }

    public bool LockInputs { get; set; }
}
