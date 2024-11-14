namespace BambuVideoStream.Models;

public class AppSettings
{
    public bool DisableUpdateCheck { get; set; } = false;
    public bool ExitOnIdle { get; set; } = true;
    public bool ExitOnEndpointDisconnect { get; set; } = true;
    public bool PrintSceneItemsAndExit { get; set; }
}
