namespace Blackbox.Domain;

public sealed record ObsSourcePlan(string Name, string Kind, AudioCategory? AudioCategory, IReadOnlyDictionary<string, string> Settings);
