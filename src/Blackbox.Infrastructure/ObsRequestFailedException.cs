namespace Blackbox.Infrastructure;

public sealed class ObsRequestFailedException : InvalidOperationException
{
    public ObsRequestFailedException(IReadOnlyList<ObsResponse> failedResponses)
        : base(BuildMessage(failedResponses))
    {
        FailedResponses = failedResponses;
    }

    public IReadOnlyList<ObsResponse> FailedResponses { get; }

    private static string BuildMessage(IReadOnlyList<ObsResponse> failures)
    {
        var details = failures.Select(static failure =>
            $"{failure.RequestType} ({failure.Code}): {failure.Comment ?? "OBS rejected the request."}");
        return $"OBS setup failed: {string.Join("; ", details)}";
    }
}
