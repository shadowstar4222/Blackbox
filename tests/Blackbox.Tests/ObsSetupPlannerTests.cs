using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;

namespace Blackbox.Tests;

public sealed class ObsSetupPlannerTests
{
    [Fact]
    public void CreateDefaultPlan_creates_sources_and_processed_mic_filters()
    {
        var planner = new ObsSetupPlanner();

        var plan = planner.CreateDefaultPlan(new RecordingSettings { RecordingLocation = "C:\\Recordings" });

        plan.Validate();
        Assert.Equal("Blackbox", plan.ProfileName);
        Assert.Equal("Blackbox Recording", plan.SceneName);
        Assert.Contains(plan.Sources, static source => source.AudioCategory == AudioCategory.Game);
        Assert.Contains(plan.Sources, static source => source.AudioCategory == AudioCategory.VoiceChat);
        Assert.Contains(plan.Sources, static source => source.AudioCategory == AudioCategory.RawMicrophone);
        Assert.Contains(plan.Sources, static source => source.AudioCategory == AudioCategory.ProcessedMicrophone);
        var gameCapture = Assert.Single(plan.Sources, static source => source.Name == "Blackbox Game Capture");
        Assert.Equal(ObsGameCaptureSettings.SpecificWindowMode, gameCapture.Settings["capture_mode"]);
        Assert.Equal("1", gameCapture.Settings["priority"]);
        Assert.Equal(5, plan.Filters.Count);
        Assert.All(plan.Filters, static filter => Assert.Equal("Blackbox Processed Microphone", filter.SourceName));
    }
}
