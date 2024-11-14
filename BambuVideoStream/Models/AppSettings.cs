namespace BambuVideoStream.Models;

public class AppSettings
{
    public bool DisableUpdateCheck { get; set; } = false;
    public bool ExitOnIdle { get; set; } = true;
    public bool ExitOnObsDisconnect { get; set; } = true;
    public bool ExitOnBambuDisconnect { get; set; } = false;
    public bool PrintSceneItemsAndExit { get; set; }
}
