namespace Blackbox.App;

public partial class RecordingLibraryPage
{
    internal async Task ValidateCompactTimelineAsync(string directory, Func<string, Task> render)
    {
        var window = System.Windows.Window.GetWindow(this);
        var oldWidth = window.Width;
        var oldHeight = window.Height;
        var markers = MarkerListBox.ItemsSource;
        var preview = PreviewImage.Source;
        var thumbnails = ThumbnailItemsControl.ItemsSource;
        RecordingTabs.SelectedItem = TimelineTab;
        var measurements = new List<object>();
        try
        {
            foreach (var size in new[] { new System.Windows.Size(1240, 980), new System.Windows.Size(1920, 1080),
                         new System.Windows.Size(1240, 780), new System.Windows.Size(1024, 680) })
            {
                window.Width = size.Width; window.Height = size.Height;
                foreach (var hasPreview in new[] { false, true })
                {
                    PreviewImage.Source = hasPreview ? preview : null;
                    ThumbnailItemsControl.ItemsSource = hasPreview ? thumbnails : null;
                    foreach (var count in new[] { 0, 3, 200 })
                    {
                        MarkerListBox.ItemsSource = Enumerable.Range(0, count)
                            .Select(index => new MarkerItem(Guid.NewGuid(), $"0:{index % 60:00}", $"Moment {index + 1}", "Manual | -30s / +10s")).ToArray();
                        TimelineDetailsPanel.ScrollToTop();
                        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        var suffix = $"{size.Width}x{size.Height}-{(hasPreview ? "preview" : "no-preview")}-{count}-markers";
                        await render("timeline-layout-" + suffix);
                        if (count == 0 && MarkerListBox.IsVisible)
                            throw new InvalidOperationException("The empty marker list still reserves a gray panel on resize.");
                        if (count > 0 && (!MarkerListBox.IsVisible || MarkerListBox.ActualHeight > 180.1 || MarkerListBox.Items.Count != count))
                            throw new InvalidOperationException("The marker list is hidden, unbounded, or lost its items.");
                        var actionTop = TimelineActions.TransformToAncestor(TimelineLayout).Transform(new System.Windows.Point()).Y;
                        if (actionTop + TimelineActions.ActualHeight > TimelineLayout.ActualHeight + 1 || !PlayButton.IsVisible)
                            throw new InvalidOperationException("Timeline actions were pushed outside the page.");
                        if (TimelineDetailsPanel.ScrollableHeight < 1)
                        {
                            var contentBottom = TimelineDetailsContent.TransformToAncestor(TimelineLayout)
                                .Transform(new System.Windows.Point(0, TimelineDetailsContent.ActualHeight)).Y;
                            if (actionTop - contentBottom > 24)
                                throw new InvalidOperationException("Expanding the timeline left a large gap before its actions.");
                        }
                        if (count == 200)
                        {
                            MarkerListBox.ScrollIntoView(MarkerListBox.Items[count - 1]);
                            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            if (MarkerListBox.ItemContainerGenerator.ContainerFromIndex(count - 1) is not System.Windows.Controls.ListBoxItem { IsVisible: true })
                                throw new InvalidOperationException("The bounded marker list cannot reach its last item.");
                            if (MarkerListBox.ItemContainerGenerator.ContainerFromIndex(0) is not null)
                                throw new InvalidOperationException("Marker rows stopped virtualizing.");
                        }
                        measurements.Add(new { Size = suffix, MarkerHeight = MarkerListBox.IsVisible ? MarkerListBox.ActualHeight : 0,
                            ContentHeight = TimelineDetailsContent.ActualHeight, LayoutHeight = TimelineLayout.ActualHeight,
                            ActionTop = actionTop, ScrollableHeight = TimelineDetailsPanel.ScrollableHeight });
                    }
                }
            }
            var detailsVisibility = TimelineDetailsPanel.Visibility;
            var emptyVisibility = LibraryEmptyState.Visibility;
            try
            {
                TimelineDetailsPanel.Visibility = System.Windows.Visibility.Collapsed;
                LibraryEmptyState.Visibility = System.Windows.Visibility.Visible;
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var emptyTop = LibraryEmptyState.TransformToAncestor(TimelineLayout).Transform(new System.Windows.Point()).Y;
                if (TimelineLayout.VerticalAlignment != System.Windows.VerticalAlignment.Stretch ||
                    Math.Abs(emptyTop - (TimelineLayout.ActualHeight - LibraryEmptyState.ActualHeight) / 2) > 1)
                    throw new InvalidOperationException("The no-selection state lost its centered layout.");
                await render("timeline-layout-no-selection");
            }
            finally
            {
                TimelineDetailsPanel.Visibility = detailsVisibility;
                LibraryEmptyState.Visibility = emptyVisibility;
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "timeline-layout-result.json"),
                System.Text.Json.JsonSerializer.Serialize(measurements, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            MarkerListBox.ItemsSource = markers; PreviewImage.Source = preview; ThumbnailItemsControl.ItemsSource = thumbnails;
            window.Width = oldWidth; window.Height = oldHeight;
            TimelineDetailsPanel.ScrollToTop();
        }
    }

    internal async Task ValidateEmbeddedPlaybackAsync(string directory, Func<Task> render, Action goBack)
    {
        var session = _sessionView?.Cast<SessionListItem>().Select(static item => item.Session)
            .FirstOrDefault(static session => session.Segments.Count >= 2 && !session.HasGaps && !session.HasMissingSegments && !session.HasDamagedSegments);
        if (session is null) throw new InvalidOperationException("Playback validation needs a healthy multi-segment test recording.");
        var segments = session.Segments.OrderBy(static segment => segment.StartTime).ToArray();
        var initial = session with { Segments = segments[..^1], EndTime = segments[^2].EndTime };
        var runtime = await _playbackRuntime.GetAsync();
        var windowsBefore = System.Windows.Application.Current.Windows.Count;
        await OpenPlaybackAsync(initial, TimeSpan.Zero);
        await _playbackPage!.ValidatePlaybackAsync(directory, session);
        await render();
        goBack();
        await _playbackRelease;
        if (_playbackPage is not null || !IsVisible || LibrarySurface.Visibility != System.Windows.Visibility.Visible)
            throw new InvalidOperationException("The main back button did not return from review to the library.");
        if (System.Windows.Application.Current.Windows.Count != windowsBefore)
            throw new InvalidOperationException("Review left a native-overlay window alive after disposal.");
        await OpenMediaFileAsync(segments[0].FilePath, "Validate MKV preview");
        await _playbackPage!.ValidateFilePreviewAsync();
        await ClosePlaybackAsync();
        var mp4 = System.IO.Path.Combine(directory, "playback-sample.mp4");
        if (System.IO.File.Exists(mp4))
        {
            await OpenMediaFileAsync(mp4, "Validate MP4 preview");
            await _playbackPage!.ValidateFilePreviewAsync();
            await ClosePlaybackAsync();
        }
        await Task.Delay(500);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(1000);
        var releasedCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, $"playback-released-{(int)ActualWidth}.txt"),
            $"After native player release: {releasedCpu:F2}% of one CPU core during a short idle sample. Shared runtime retained.");
        if (!ReferenceEquals(runtime, await _playbackRuntime.GetAsync()))
            throw new InvalidOperationException("Opening review replaced the shared decode runtime.");
    }

    internal async Task ValidateSessionFiltersAsync()
    {
        if (_sessionView is null) throw new InvalidOperationException("Recording view was not initialized.");
        var count = _sessionView.Cast<object>().Count();
        if (count > 0)
        {
            var selected = _sessionView.Cast<SessionListItem>().First();
            SessionSearchBox.Text = selected.Title;
            if (!_sessionView.Cast<SessionListItem>().Any(item => item.Session.Id == selected.Session.Id))
                throw new InvalidOperationException("Application search lost its matching session.");
            SessionSearchBox.Clear();
        }
        SessionSearchBox.Text = "__blackbox_no_such_recording__";
        if (_sessionView.Cast<object>().Any()) throw new InvalidOperationException("Recording search did not filter its results.");
        SessionSearchBox.Clear();
        if (_sessionView.Cast<object>().Count() != count) throw new InvalidOperationException("Clearing search did not restore recordings.");
        SessionFilterComboBox.SelectedIndex = 3;
        if (_sessionView.Cast<SessionListItem>().Any(item => !item.Session.HasGaps && !item.Session.HasMissingSegments && !item.Session.HasDamagedSegments))
            throw new InvalidOperationException("Media-health filter returned a healthy session.");
        SessionFilterComboBox.SelectedIndex = 0;
        SessionSortComboBox.SelectedIndex = 2;
        var durations = _sessionView.Cast<SessionListItem>().Select(static item => item.Session.Duration).ToArray();
        if (!durations.SequenceEqual(durations.OrderDescending()))
            throw new InvalidOperationException("Duration sorting returned the wrong order.");
        SessionSortComboBox.SelectedIndex = 0;
        if (_sessionView.Cast<SessionListItem>().MaxBy(static item => item.Session.Duration) is { } preview)
        {
            SessionListBox.SelectedItem = preview;
            await LoadTimelineAssetsAsync(preview.Session, generateIfMissing: true);
            if (!preview.Session.HasGaps && !preview.Session.HasMissingSegments && !preview.Session.HasDamagedSegments)
            {
                var cached = await _timelineAssetService.TryGetCachedAsync(preview.Session);
                if (cached is null || cached.Thumbnails.Count == 0 || _thumbnails.Count == 0)
                    throw new InvalidOperationException("Timeline thumbnails were not generated and cached.");
                if (preview.Session.Segments.All(static segment => string.IsNullOrWhiteSpace(segment.AudioTrackLayout)) && cached.Waveform.Count != 0)
                    throw new InvalidOperationException("A video-only timeline unexpectedly generated an audio waveform.");
            }
        }
    }
}
