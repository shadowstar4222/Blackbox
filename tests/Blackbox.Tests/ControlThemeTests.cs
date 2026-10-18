using System.Xml.Linq;

namespace Blackbox.Tests;

public sealed class ControlThemeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("CheckBox")]
    [InlineData("Slider")]
    [InlineData("Expander")]
    [InlineData("TextBox")]
    [InlineData("ProgressBar")]
    [InlineData("ToolTip")]
    [InlineData("Button")]
    public void Shared_controls_replace_the_native_chrome(string type)
    {
        var style = ImplicitStyle(type);
        Assert.Contains(style.Elements(), element => element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Template" && element.Descendants().Any(child => child.Name.LocalName == "ControlTemplate"));
    }

    [Fact]
    public void Checkbox_covers_all_toggle_and_interaction_states()
    {
        var style = ImplicitStyle("CheckBox");
        var triggers = style.Descendants().Where(element => element.Name.LocalName == "Trigger").ToArray();
        foreach (var state in new[] { "True", "{x:Null}" })
            Assert.Contains(triggers, element => (string?)element.Attribute("Property") == "IsChecked" && (string?)element.Attribute("Value") == state);
        foreach (var property in new[] { "IsMouseOver", "IsPressed", "IsKeyboardFocused", "IsEnabled" })
            Assert.Contains(triggers, element => (string?)element.Attribute("Property") == property);
    }

    [Fact]
    public void Theme_preserves_required_native_parts_and_virtualization()
    {
        foreach (var (type, part) in new[] { ("Slider", "PART_Track"), ("TextBox", "PART_ContentHost"),
                     ("ProgressBar", "PART_Track"), ("ProgressBar", "PART_Indicator"), ("ComboBox", "PART_EditableTextBox"), ("ComboBox", "PART_Popup") })
            Assert.Contains(ImplicitStyle(type).Descendants(), element => (string?)element.Attribute(Xaml + "Name") == part);
        Assert.Contains(ImplicitStyle("ListBox").Elements(), element =>
            (string?)element.Attribute("Property") == "VirtualizingPanel.VirtualizationMode" && (string?)element.Attribute("Value") == "Recycling");
    }

    [Fact]
    public void Player_uses_shared_button_and_input_templates()
    {
        var page = Load("PlaybackPage.xaml");
        Assert.DoesNotContain(page.Descendants().Where(element => element.Name.LocalName == "Style"),
            style => (string?)style.Attribute("TargetType") == "TextBox" || (string?)style.Attribute(Xaml + "Key") == "IconButtonStyle");
        Assert.Contains(page.Descendants().Attributes("Style"), attribute => attribute.Value == "{StaticResource ToolbarButtonStyle}");
        Assert.Contains(page.Descendants(), element => element.Name.LocalName == "VideoView" && (string?)element.Attribute("Background") == "Black");
    }

    private static XElement ImplicitStyle(string type) => Assert.Single(Load(Path.Combine("Themes", "BlackboxTheme.xaml"))
        .Root!.Elements().Where(element => element.Name.LocalName == "Style" && element.Attribute(Xaml + "Key") is null &&
            (string?)element.Attribute("TargetType") == $"{{x:Type {type}}}"));

    private static XDocument Load(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Blackbox.sln"))) directory = directory.Parent;
        return XDocument.Load(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), "src", "Blackbox.App", file));
    }
}
