namespace Blackbox.Domain;

public sealed record AudioLevelSnapshot(string SourceName, double PeakDb, double RmsDb, DateTimeOffset CapturedAt);
