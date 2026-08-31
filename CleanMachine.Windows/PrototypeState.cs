namespace CleanMachine.Windows;

public sealed record PrototypeActivity(DateTimeOffset Time, string Title, string Detail, string Tone);

public sealed class PrototypeState
{
    public bool BackgroundAgentEnabled { get; set; } = true;
    public bool CleanOnBrowserExit { get; set; } = true;
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public DateTimeOffset? LastCleanup { get; set; }
    public List<PrototypeActivity> Activity { get; } = [];

    public void Record(string title, string detail, string tone = "green") => Activity.Insert(0, new PrototypeActivity(DateTimeOffset.Now, title, detail, tone));
}
