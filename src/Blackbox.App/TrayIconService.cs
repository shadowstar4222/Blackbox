using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Blackbox.App;

public enum TrayCommand
{
    Show,
    StartRecording,
    StopRecording,
    ProtectRecent,
    ToggleAutomaticCapture,
    OpenRecordings,
    Exit
}

public enum TrayNotificationKind
{
    Information,
    Warning,
    Error
}

public sealed record TrayIconState(
    bool IsRecording,
    bool IsAutomaticCaptureEnabled,
    bool IsRecorderReady,
    string? CurrentGame)
{
    public bool CanToggleAutomaticCapture { get; init; } = true;
}

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _startItem;
    private readonly Forms.ToolStripMenuItem _stopItem;
    private readonly Forms.ToolStripMenuItem _automaticItem;
    private Drawing.Icon? _icon;
    private bool _hasShownBackgroundTip;
    private bool _isDisposed;

    public TrayIconService()
    {
        _icon = LoadApplicationIcon();
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = Drawing.Color.FromArgb(34, 38, 43),
            ForeColor = Drawing.Color.FromArgb(244, 244, 245),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Renderer = new Forms.ToolStripProfessionalRenderer(new BlackboxMenuColors()) { RoundedEdges = false }
        };

        var showItem = AddItem(menu, "Open Blackbox", TrayCommand.Show);
        showItem.Font = new Drawing.Font(showItem.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _startItem = AddItem(menu, "Start recording", TrayCommand.StartRecording);
        _stopItem = AddItem(menu, "Stop recording", TrayCommand.StopRecording);
        AddItem(menu, "Protect previous 5 minutes", TrayCommand.ProtectRecent);
        _automaticItem = AddItem(menu, "Watch remembered games", TrayCommand.ToggleAutomaticCapture);
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem(menu, "Open recordings", TrayCommand.OpenRecordings);
        AddItem(menu, "Exit Blackbox", TrayCommand.Exit);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _icon,
            Text = "Blackbox - starting",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => CommandRequested?.Invoke(TrayCommand.Show);
    }

    public event Action<TrayCommand>? CommandRequested;

    public void Update(TrayIconState state)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _startItem.Enabled = state.IsRecorderReady &&
            !state.IsRecording &&
            !state.IsAutomaticCaptureEnabled;
        _stopItem.Enabled = state.IsRecording;
        _automaticItem.Checked = state.IsAutomaticCaptureEnabled;
        _automaticItem.Enabled = state.CanToggleAutomaticCapture;
        var stateText = state.IsRecording
            ? "recording"
            : state.IsAutomaticCaptureEnabled
                ? "watching games"
                : state.IsRecorderReady
                    ? "ready"
                    : "recorder needs attention";
        var gameText = string.IsNullOrWhiteSpace(state.CurrentGame)
            ? string.Empty
            : $" - {state.CurrentGame}";
        _notifyIcon.Text = TruncateTooltip($"Blackbox - {stateText}{gameText}");
    }

    public void ShowBackgroundTip()
    {
        if (_hasShownBackgroundTip || _isDisposed)
        {
            return;
        }

        _hasShownBackgroundTip = true;
        _notifyIcon.BalloonTipTitle = "Blackbox is still running";
        _notifyIcon.BalloonTipText =
            "Recording controls and background services remain available in the notification area.";
        _notifyIcon.ShowBalloonTip(3500);
    }

    public void ShowNotification(
        string title,
        string message,
        TrayNotificationKind kind = TrayNotificationKind.Information)
    {
        if (_isDisposed)
        {
            return;
        }

        _notifyIcon.BalloonTipIcon = kind switch
        {
            TrayNotificationKind.Error => Forms.ToolTipIcon.Error,
            TrayNotificationKind.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info
        };
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message.Length <= 255
            ? message
            : message[..252] + "...";
        _notifyIcon.ShowBalloonTip(5000);
    }

    private Forms.ToolStripMenuItem AddItem(
        Forms.ContextMenuStrip menu,
        string text,
        TrayCommand command)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => CommandRequested?.Invoke(command);
        menu.Items.Add(item);
        return item;
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var extracted = Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (extracted is not null)
            {
                return extracted;
            }
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static string TruncateTooltip(string value) =>
        value.Length <= 63 ? value : value[..60] + "...";

    private sealed class BlackboxMenuColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(34, 38, 43);
        private static readonly Drawing.Color Selected = Drawing.Color.FromArgb(38, 59, 71);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(54, 60, 67);
        private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(44, 169, 223);

        public BlackboxMenuColors() => UseSystemColors = false;
        public override Drawing.Color ToolStripDropDownBackground => Surface;
        public override Drawing.Color ImageMarginGradientBegin => Surface;
        public override Drawing.Color ImageMarginGradientMiddle => Surface;
        public override Drawing.Color ImageMarginGradientEnd => Surface;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Accent;
        public override Drawing.Color MenuItemSelected => Selected;
        public override Drawing.Color MenuItemSelectedGradientBegin => Selected;
        public override Drawing.Color MenuItemSelectedGradientEnd => Selected;
        public override Drawing.Color MenuItemPressedGradientBegin => Selected;
        public override Drawing.Color MenuItemPressedGradientMiddle => Selected;
        public override Drawing.Color MenuItemPressedGradientEnd => Selected;
        public override Drawing.Color CheckBackground => Accent;
        public override Drawing.Color CheckSelectedBackground => Accent;
        public override Drawing.Color CheckPressedBackground => Accent;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Surface;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
        _icon = null;
    }
}
