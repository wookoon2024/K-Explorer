namespace WorkFileExplorer.App.Models;

public sealed class AiTerminalTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string DefaultPrompt { get; set; } = string.Empty;
    public bool AutoRespondEnabled { get; set; } = true;
    public string AutoNumberResponse { get; set; } = "2";
    public string AutoYesNoResponse { get; set; } = "y";
}
