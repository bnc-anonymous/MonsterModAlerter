using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace MyPlugin;

public class MyPluginSettings : ISettings
{
    //Mandatory setting to allow enabling/disabling your plugin
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    // Comma-separated mod or buff strings to search for on monsters
    public string ModsOrBuffsToSearchFor { get; set; } = "TormentHauntedMonsterThief";
}