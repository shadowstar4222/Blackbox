using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Blackbox.App;

internal static class ThemeUiValidation
{
    internal static async Task ValidateAsync(Window owner, string directory)
    {
        var body = new StackPanel { Margin = new Thickness(24) };
        var window = new Window
        {
            Owner = owner, ShowInTaskbar = false, Title = "Blackbox control validation",
            Width = 880, Height = 620, UseLayoutRounding = true, Content = body
        };
        void Heading(string text) => body.Children.Add(new TextBlock
        { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 8) });
        WrapPanel Row(params UIElement[] controls)
        {
            var row = new WrapPanel();
            foreach (var control in controls)
            {
                if (control is FrameworkElement element) element.Margin = new Thickness(0, 0, 16, 8);
                row.Children.Add(control);
            }
            body.Children.Add(row);
            return row;
        }
        var accent = ((SolidColorBrush)owner.FindResource("AccentBrush")).Color;
        var check = new CheckBox { Content = "Record automatically", IsThreeState = true };
        var mirror = new CheckBox { Content = "Bound setting" };
        mirror.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = check, Mode = BindingMode.TwoWay });
        var slider = new Slider { Minimum = -30, Maximum = 30, Value = 0, Width = 260, IsSnapToTickEnabled = true, TickFrequency = 1 };
        var input = new TextBox { Width = 180, Text = "Blackbox" };
        var combo = new ComboBox { Width = 180, IsEditable = true, Text = "Custom quality" };
        combo.Items.Add("High quality"); combo.Items.Add("Balanced");
        var progress = new ProgressBar { Width = 260, Height = 6, Value = 25 };
        var expander = new Expander { Header = "Advanced controls", Content = new TextBlock { Text = "Expanded settings", Margin = new Thickness(24, 8, 0, 8) } };
        Heading("BLACKBOX / CONTROL THEME");
        Row(check, mirror, new CheckBox { Content = "Game audio", IsChecked = true },
            new CheckBox { Content = "Unavailable", IsEnabled = false, IsChecked = true });
        Row(new Button { Content = "Record", Style = (Style)owner.FindResource("PrimaryButtonStyle") },
            new Button { Content = "Stop", Style = (Style)owner.FindResource("DangerButtonStyle") },
            new Button { Content = "Apply settings" }, new Button { Content = "Unavailable", IsEnabled = false },
            new Button { Content = "\uE72C", ToolTip = "Refresh", Style = (Style)owner.FindResource("IconButtonStyle") },
            new ToggleButton { IsChecked = true, Style = (Style)owner.FindResource("SwitchStyle") });
        Heading("Input and recording levels");
        Row(slider, new Slider { Width = 180, Value = 7, IsEnabled = false });
        Row(input, combo, new TextBox { Text = "Unavailable", Width = 180, IsEnabled = false });
        Row(progress, new ProgressBar { Width = 180, Height = 6, Value = 65, IsEnabled = false });
        body.Children.Add(expander);
        var tabs = new TabControl { Height = 100, Margin = new Thickness(0, 8, 0, 0) };
        tabs.Items.Add(new TabItem { Header = "Routing", Content = new TextBlock { Text = "Microphone and system audio", Margin = new Thickness(12) } });
        tabs.Items.Add(new TabItem { Header = "Voice" });
        tabs.Items.Add(new TabItem { Header = "Unavailable", IsEnabled = false });
        body.Children.Add(tabs);
        try
        {
            window.Show();
            await SettleAsync();
            T Part<T>(Control control, string name) where T : DependencyObject =>
                control.Template.FindName(name, control) as T ?? throw new InvalidOperationException($"Themed {control.GetType().Name} is missing {name}.");
            var toggle = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle);
            toggle.Toggle();
            await SettleAsync();
            if (check.IsChecked != true || mirror.IsChecked != true ||
                Part<TextBlock>(check, "CheckGlyph").Visibility != Visibility.Visible ||
                ((SolidColorBrush)Part<Border>(check, "CheckSurface").Background).Color != accent)
                throw new InvalidOperationException("The themed checkbox lost its checked visual or two-way binding.");
            toggle.Toggle();
            await SettleAsync();
            if (check.IsChecked is not null || Part<Border>(check, "MixedGlyph").Visibility != Visibility.Visible)
                throw new InvalidOperationException("The indeterminate checkbox has no themed visual.");
            toggle.Toggle();
            await SettleAsync();
            if (check.IsChecked != false || mirror.IsChecked != false || Part<TextBlock>(check, "CheckGlyph").Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("The themed checkbox did not return to unchecked.");
            check.Focus();
            await SettleAsync();
            if (!check.IsKeyboardFocused || ((SolidColorBrush)Part<Border>(check, "FocusRing").BorderBrush).Color != accent)
                throw new InvalidOperationException("Checkbox keyboard focus is not visible.");
            var track = Part<Track>(slider, "PART_Track");
            Slider.IncreaseSmall.Execute(null, slider);
            if (slider.Value != 1) throw new InvalidOperationException("Slider keyboard stepping failed.");
            ((System.Windows.Input.RoutedCommand)track.IncreaseRepeatButton.Command).Execute(null, track.IncreaseRepeatButton);
            if (slider.Value <= 1) throw new InvalidOperationException("Slider rail command failed.");
            var beforeDrag = slider.Value;
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(40, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(40, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            if (slider.Value <= beforeDrag || slider.Value % 1 != 0)
                throw new InvalidOperationException("Slider drag or snap-to-tick behavior failed.");
            Part<ScrollViewer>(input, "PART_ContentHost");
            var editor = Part<TextBox>(combo, "PART_EditableTextBox");
            editor.Text = "60 fps";
            await SettleAsync();
            if (combo.Text != "60 fps") throw new InvalidOperationException("Editable quality selection lost its text binding.");
            combo.IsDropDownOpen = true;
            await SettleAsync();
            if (!Part<Popup>(combo, "PART_Popup").IsOpen) throw new InvalidOperationException("The themed dropdown did not open.");
            combo.IsDropDownOpen = false;
            var indicator = Part<Border>(progress, "PART_Indicator");
            if (Math.Abs(indicator.ActualWidth / progress.ActualWidth - 0.25) > 0.02)
                throw new InvalidOperationException("The themed audio meter has an incorrect fill width.");
            progress.Value = 75;
            await SettleAsync();
            if (Math.Abs(indicator.ActualWidth / progress.ActualWidth - 0.75) > 0.02)
                throw new InvalidOperationException("The audio meter did not track its level.");
            progress.IsIndeterminate = true;
            await Task.Delay(150);
            await SettleAsync();
            if (!indicator.HasAnimatedProperties || indicator.ActualWidth < progress.ActualWidth * 0.95)
                throw new InvalidOperationException("Visible progress feedback is missing.");
            progress.Visibility = Visibility.Collapsed;
            await SettleAsync();
            if (indicator.HasAnimatedProperties) throw new InvalidOperationException("Hidden progress feedback retained an animation clock.");
            progress.IsIndeterminate = false; progress.Visibility = Visibility.Visible;
            var expansion = (IExpandCollapseProvider)new ExpanderAutomationPeer(expander).GetPattern(PatternInterface.ExpandCollapse);
            expansion.Expand();
            await SettleAsync();
            if (!expander.IsExpanded || !Part<ContentPresenter>(expander, "ExpandedContent").IsVisible)
                throw new InvalidOperationException("The themed expander did not expose its content.");
            check.IsChecked = true;
            await SettleAsync();
            var bitmap = new RenderTargetBitmap((int)body.ActualWidth, (int)body.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(body);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "control-theme.png"))) encoder.Save(file);
            File.WriteAllText(Path.Combine(directory, "control-theme-result.txt"),
                "PASS: checked, unchecked, mixed, disabled and keyboard-focus visuals; native automation and two-way checkbox binding; slider keyboard/rail/drag/snap; editable dropdown; determinate meter; visible-only progress animation; expander automation.");
        }
        finally { window.Close(); }
    }

    private static async Task SettleAsync() =>
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
