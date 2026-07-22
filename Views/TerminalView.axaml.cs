using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Googlook.Controls;
using Googlook.Services;

namespace Googlook.Views;

/// <summary>
/// The Gemini CLI tab. Runs the CLI inside a ConPTY, feeds its output through a
/// VT emulator (<see cref="VtScreen"/>), and paints the result on a
/// <see cref="TerminalSurface"/>. Keystrokes are forwarded raw — arrows, Tab, Enter,
/// Backspace, and Ctrl-combos — so interactive prompts work. Per-account isolation:
/// the child's HOME/USERPROFILE point at the account's profile folder.
/// </summary>
public partial class TerminalView : UserControl
{
    public static readonly StyledProperty<string?> ProfileDirProperty =
        AvaloniaProperty.Register<TerminalView, string?>(nameof(ProfileDir));

    public static readonly StyledProperty<string> CommandProperty =
        AvaloniaProperty.Register<TerminalView, string>(nameof(Command), "gemini");

    public string? ProfileDir
    {
        get => GetValue(ProfileDirProperty);
        set => SetValue(ProfileDirProperty, value);
    }

    public string Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private readonly ConPtySession _session = new();
    private VtScreen _screen = new(120, 30);
    private readonly DispatcherTimer _timer;
    private TerminalSurface _surface = null!;
    private TextBlock _profileLabel = null!;
    private bool _dirty;
    private bool _started;

    public TerminalView()
    {
        AvaloniaXamlLoader.Load(this);
        _surface = this.FindControl<TerminalSurface>("Surface")!;
        _profileLabel = this.FindControl<TextBlock>("ProfileLabel")!;

        _session.Output += chunk => { _screen.Feed(chunk); _dirty = true; };
        _surface.SizeChanged += (_, _) => SyncSize();
        PointerPressed += (_, _) => Focus();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            var (cells, cx, cy, vis) = _screen.Snapshot();
            _surface.SetSnapshot(cells, cx, cy, vis);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsVisible) TryStart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _session.Dispose();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            TryStart();
    }

    private (short cols, short rows) GridSize()
    {
        var w = _surface.Bounds.Width;
        var h = _surface.Bounds.Height;
        var cols = (short)Math.Max(20, w > 1 ? (int)(w / _surface.CellWidth) : 120);
        var rows = (short)Math.Max(6, h > 1 ? (int)(h / _surface.CellHeight) : 30);
        return (cols, rows);
    }

    private void TryStart()
    {
        if (_started) return;
        _started = true;

        var (cols, rows) = GridSize();
        _screen = new VtScreen(cols, rows);

        var dir = string.IsNullOrWhiteSpace(ProfileDir) ? BrowserProfile.DirFor("default") : ProfileDir!;
        Directory.CreateDirectory(dir);
        _profileLabel.Text = dir;

        var cmd = string.IsNullOrWhiteSpace(Command) ? "gemini" : Command;
        _screen.Feed($"Starting \"{cmd}\" (profile: {dir})\r\n\r\n");

        var commandLine = $"cmd.exe /k set \"USERPROFILE={dir}\" && set \"HOME={dir}\" && {cmd}";
        try
        {
            _session.Start(commandLine, dir, cols, rows);
            _timer.Start();
            _dirty = true;
            Focus();
        }
        catch (Exception ex)
        {
            _screen.Feed("\r\n[could not start terminal] " + ex.Message +
                         "\r\nNeeds Windows 10 1809+ with the Gemini CLI installed.\r\n");
            _dirty = true;
        }
    }

    private void SyncSize()
    {
        if (!_started) return;
        var (cols, rows) = GridSize();
        if (cols == _screen.Cols && rows == _screen.Rows) return;
        _screen.Resize(cols, rows);
        _session.Resize(cols, rows);
        _dirty = true;
    }

    // ---- raw key + text forwarding --------------------------------------

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text)) { _session.Write(e.Text); e.Handled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var seq = Translate(e);
        if (seq is null) return;
        _session.Write(seq);
        e.Handled = true;
    }

    private static string? Translate(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl)
        {
            // Ctrl-A..Ctrl-Z → control bytes 0x01..0x1A
            if (e.Key >= Key.A && e.Key <= Key.Z)
                return ((char)(e.Key - Key.A + 1)).ToString();
        }

        return e.Key switch
        {
            Key.Enter     => "\r",
            Key.Back      => "\x7f",
            Key.Tab       => "\t",
            Key.Escape    => "\x1b",
            Key.Up        => "\x1b[A",
            Key.Down      => "\x1b[B",
            Key.Right     => "\x1b[C",
            Key.Left      => "\x1b[D",
            Key.Home      => "\x1b[H",
            Key.End       => "\x1b[F",
            Key.PageUp    => "\x1b[5~",
            Key.PageDown  => "\x1b[6~",
            Key.Delete    => "\x1b[3~",
            Key.Insert    => "\x1b[2~",
            _             => null, // printable chars arrive via OnTextInput
        };
    }
}
