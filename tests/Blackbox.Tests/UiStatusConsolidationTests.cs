using System.Xml.Linq;

namespace Blackbox.Tests;

public sealed class UiStatusConsolidationTests
{
    [Fact]
    public void Shell_has_one_status_label_and_no_legacy_duplicate_widgets()
    {
        var shell = Load("MainWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = shell.Descendants().Select(element => (string?)element.Attribute(x + "Name")).ToArray();
        Assert.Single(names.Where(name => name == "SidebarStatusText"));
        Assert.DoesNotContain(names, name => name is "ObsStatusText" or "HeaderGameText" or
            "GuardianStateText" or "GuardianTargetText" or "AutomaticCaptureStatusText" or "CurrentViewSubtitleText");
        Assert.DoesNotContain(shell.Descendants().Attributes(), attribute => attribute.Value.Contains("ElementName=SidebarStatus", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_does_not_add_a_second_back_button_or_permanent_local_label()
    {
        var review = Load("PlaybackPage.xaml");
        Assert.DoesNotContain(review.Descendants().Attributes("Click"), attribute => attribute.Value == "BackButton_Click");
        Assert.DoesNotContain(review.Descendants().Attributes("Text"), attribute => attribute.Value == "Local playback");
        var shell = Load("MainWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Single(shell.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "BackButton"));
    }

    private static XDocument Load(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Blackbox.sln"))) directory = directory.Parent;
        return XDocument.Load(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), "src", "Blackbox.App", file));
    }

    [Fact]
    public void Capture_inspector_has_useful_defaults_without_duplicate_overall_status()
    {
        var shell = Load("MainWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "CaptureHealthSummaryText", "AudioSignalText" })
        {
            var element = Assert.Single(shell.Descendants().Where(element => (string?)element.Attribute(x + "Name") == name));
            Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("Text")));
        }
    }

    [Fact]
    public void Microphone_separates_voice_routing_and_calibration_and_uses_one_voice_apply()
    {
        var page = Load("MicrophonePage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabs = page.Descendants().Where(element => element.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(new[] { "Voice", "Routing", "Calibration" }, tabs.Select(tab => (string?)tab.Attribute("Header")));
        Assert.Contains(tabs[1].Descendants(), element => (string?)element.Attribute(x + "Name") == "ExcludedDevicesList");
        Assert.Contains(tabs[2].Descendants(), element => (string?)element.Attribute(x + "Name") == "CalibrationMeasurementsPanel");
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(x + "Name") == "ApplyGainButton");
        Assert.Single(page.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "ApplyVoiceButton"));
    }

    [Fact]
    public void Diagnostic_dump_is_not_in_the_events_tab()
    {
        var page = Load("DiagnosticsPage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabs = page.Descendants().Where(element => element.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(new[] { "Events", "Recording setup", "Technical details" }, tabs.Select(tab => (string?)tab.Attribute("Header")));
        Assert.Contains(tabs[2].Descendants(), element => (string?)element.Attribute(x + "Name") == "RecorderSummaryText");
        Assert.DoesNotContain(tabs[0].Descendants(), element => (string?)element.Attribute(x + "Name") == "RecorderSummaryText");
    }

    [Fact]
    public void Diagnostic_events_use_virtualized_pixel_scrolling_for_wrapped_rows()
    {
        var page = Load("DiagnosticsPage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var list = Assert.Single(page.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "LogListBox"));
        Assert.Equal("Pixel", (string?)list.Attribute("VirtualizingPanel.ScrollUnit"));
        Assert.Equal("True", (string?)list.Attribute("ScrollViewer.CanContentScroll"));
        Assert.Equal("True", (string?)list.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)list.Attribute("VirtualizingPanel.VirtualizationMode"));
    }

    [Fact]
    public void Timeline_marker_list_is_compact_and_hidden_when_empty()
    {
        var page = Load("RecordingLibraryPage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var list = Assert.Single(page.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "MarkerListBox"));
        Assert.Equal("180", (string?)list.Attribute("MaxHeight"));
        Assert.Equal("Stretch", (string?)list.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Top", (string?)list.Attribute("VerticalAlignment"));
        Assert.Equal("Transparent", (string?)list.Attribute("Background"));
        Assert.Equal("Pixel", (string?)list.Attribute("VirtualizingPanel.ScrollUnit"));
        Assert.Contains(list.Descendants(), element => element.Name.LocalName == "Style" &&
            (string?)element.Attribute("BasedOn") == "{StaticResource {x:Type ListBox}}");
        var empty = Assert.Single(list.Descendants().Where(element => element.Name.LocalName == "Trigger" &&
            (string?)element.Attribute("Property") == "HasItems" && (string?)element.Attribute("Value") == "False"));
        Assert.Contains(empty.Elements(), element => (string?)element.Attribute("Property") == "Visibility" &&
            (string?)element.Attribute("Value") == "Collapsed");
    }

    [Fact]
    public void Timeline_details_do_not_stretch_empty_rows_on_resize()
    {
        var page = Load("RecordingLibraryPage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var content = Assert.Single(page.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "TimelineDetailsContent"));
        Assert.Equal("Top", (string?)content.Attribute("VerticalAlignment"));
        Assert.DoesNotContain(content.Elements().First(element => element.Name.LocalName == "Grid.RowDefinitions").Elements(),
            row => (string?)row.Attribute("Height") == "*");
        Assert.Single(page.Descendants().Where(element => (string?)element.Attribute(x + "Name") == "TimelineActions"));
    }
}
