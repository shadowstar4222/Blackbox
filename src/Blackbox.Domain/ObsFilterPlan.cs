namespace Blackbox.Domain;

public sealed record ObsFilterPlan(string SourceName, string Name, string Kind, IReadOnlyDictionary<string, string> Settings);
