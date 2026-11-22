using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LLMTray.App;

/// <summary>
/// The building blocks of the Mac-style settings pages: a section title, a
/// card, and rows that read "what (and a line of help) — value or action".
/// </summary>
static class Ui
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static T Resource<T>(string key, T fallback) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is T found ? found : fallback;

    public static Brush Secondary => Resource<Brush>("TextFillColorSecondaryBrush", new SolidColorBrush(Colors.Gray));
    public static Brush Tertiary => Resource<Brush>("TextFillColorTertiaryBrush", new SolidColorBrush(Colors.DarkGray));
    public static Brush Stroke => Resource<Brush>("CardStrokeColorDefaultBrush", new SolidColorBrush(Colors.LightGray));
    public static Brush CardBackground => Resource<Brush>("CardBackgroundFillColorDefaultBrush", new SolidColorBrush(Colors.White));
    public static Brush Accent => Resource<Brush>("AccentTextFillColorPrimaryBrush", new SolidColorBrush(Colors.SteelBlue));

    /// <summary>The app's theme now (the colours below follow it; pages render again on a change).</summary>
    public static bool Dark => Application.Current.RequestedTheme == ApplicationTheme.Dark;

    // Status colours, readable as 12 px text on a light or a dark card.
    public static Brush Green => new SolidColorBrush(Dark ? ColorHelper.FromArgb(255, 0x4A, 0xC2, 0x6B) : ColorHelper.FromArgb(255, 0x1A, 0x7F, 0x37));
    public static Brush Orange => new SolidColorBrush(Dark ? ColorHelper.FromArgb(255, 0xF0, 0xA0, 0x30) : ColorHelper.FromArgb(255, 0x9A, 0x5B, 0x00));
    public static Brush Red => new SolidColorBrush(Dark ? ColorHelper.FromArgb(255, 0xFF, 0x6B, 0x6B) : ColorHelper.FromArgb(255, 0xC4, 0x2B, 0x1C));
    public static Brush Gray => new SolidColorBrush(Colors.Gray);

    /// <summary>The one padding every card row uses.</summary>
    public static readonly Thickness RowPadding = new(16, 10, 16, 10);

    public static TextBlock Section(string title) => new()
    {
        Text = title,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(4, 14, 0, 2),
    };

    /// <summary>A card: the rows with hairlines between them.</summary>
    public static Border Card(params UIElement?[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows.Where(r => r is not null))
        {
            if (stack.Children.Count > 0) stack.Children.Add(new Rectangle { Height = 1, Fill = Stroke, Margin = new Thickness(16, 0, 16, 0) });
            stack.Children.Add(row);
        }
        return new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Background = CardBackground,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>"Title / help — control".</summary>
    public static Grid Row(string title, string? help, UIElement? right)
    {
        var grid = new Grid { Padding = RowPadding, ColumnSpacing = 16, MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        left.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (help is not null) left.Children.Add(Caption(help));
        grid.Children.Add(left);
        if (right is FrameworkElement r)
        {
            r.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(r, 1);
            grid.Children.Add(r);
            // Beside a paragraph of help the control stays by the title (Mac: the same), not afloat in the middle of it.
            left.SizeChanged += (_, e) =>
            {
                var beside = e.NewSize.Height > TallText ? VerticalAlignment.Top : VerticalAlignment.Center;
                if (r.VerticalAlignment != beside) r.VerticalAlignment = beside;
            };
        }
        return grid;
    }

    /// <summary>A row's text taller than this (a title and three lines of help, or something under them) has its control at the top.</summary>
    const double TallText = 64;

    /// <summary>A path or address in full, selectable, in the code font.</summary>
    public static TextBlock PathText(string text) => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = 13,
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>"Title / help" with a wide control under them, the card's full width (a prompt, a long path).</summary>
    public static Grid StackedRow(string title, string? help, FrameworkElement below, UIElement? right = null)
    {
        var grid = Row(title, help, right);
        var left = (StackPanel)grid.Children[0];
        below.Margin = new Thickness(0, 6, 0, 0);
        below.HorizontalAlignment = HorizontalAlignment.Stretch;
        left.Children.Add(below);
        return grid;
    }

    /// <summary>A row of buttons: some on the left, some on the right.</summary>
    public static Grid ButtonRow(UIElement[] left, UIElement[] right)
    {
        var grid = new Grid { Padding = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Horizontal(left));
        var r = Horizontal(right);
        Grid.SetColumn(r, 1);
        grid.Children.Add(r);
        return grid;
    }

    public static StackPanel Horizontal(params UIElement[] items)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items) stack.Children.Add(item);
        return stack;
    }

    public static TextBlock Caption(string text, Brush? brush = null) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = brush ?? Secondary,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A value on the right of a row; a long one is cut and shown whole in its tooltip.</summary>
    public static TextBlock Value(string text, bool mono = false)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = Secondary,
            FontFamily = mono ? Mono : FontFamily.XamlAutoFontFamily,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
            IsTextSelectionEnabled = true,
        };
        ToolTipService.SetToolTip(block, text);
        return block;
    }

    public static Button Button(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = Resource<Style>("AccentButtonStyle", button.Style);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Style = Resource<Style>("SubtleButtonStyle", null!),
        };
        ToolTipService.SetToolTip(button, tip);
        // A glyph says nothing to a screen reader: the tip is its name.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    public static ToggleSwitch Toggle(bool on, Action<bool> changed, string name)
    {
        // No "On" / "Off" beside the knob: Windows writes them in its own language whatever the app's, and
        // their room kept the switch from lining up with the buttons and lists at a row's right edge (Mac:
        // no words either). The template keeps 12 px for them after the knob: the margin takes that back.
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, 0, -12, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    /// <summary>
    /// Every element under <paramref name="root"/> that the app's code put there: a panel's
    /// children, a border's or a button's content, a list's items. For the checks that nothing
    /// is made again while only a value changes (Screenshots.cs).
    /// </summary>
    public static List<object> Elements(object root)
    {
        var found = new List<object>();
        void Walk(object? node)
        {
            if (node is null or string) return;
            found.Add(node);
            switch (node)
            {
                case Panel panel:
                    foreach (var child in panel.Children) Walk(child);
                    break;
                case Border border:
                    Walk(border.Child);
                    break;
                case ItemsControl list:
                    foreach (var item in list.Items) Walk(item);
                    break;
                case ContentControl control:
                    Walk(control.Content);
                    break;
            }
        }
        Walk(root);
        return found;
    }

    public static Ellipse Dot(Brush brush, double size = 9) => new() { Width = size, Height = size, Fill = brush, VerticalAlignment = VerticalAlignment.Center };

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing to open it with: nothing more to do.
        }
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Open(folder);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(CultureInfo.CurrentCulture, "{0:0} MB", bytes / (double)(1L << 20)),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} KB", bytes / 1024),
    };

    /// <summary>The window's scale: physical pixels per DIP.</summary>
    public static double Scale(Window window)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
        return scale > 0 ? scale : 1;
    }

    /// <summary>A window of <paramref name="width"/> × <paramref name="height"/> DIPs in the middle of its screen, never bigger than it.</summary>
    public static void Center(Window window, int width, int height, int minWidth = 0, int minHeight = 0)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
        if (scale <= 0) scale = 1;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var w = (int)Math.Min(width * scale, area.Width * 0.95);
        var h = (int)Math.Min(height * scale, area.Height * 0.95);
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter && minWidth > 0)
        {
            presenter.PreferredMinimumWidth = (int)Math.Min(minWidth * scale, w);
            presenter.PreferredMinimumHeight = (int)Math.Min(minHeight * scale, h);
        }
    }

    /// <summary>
    /// Opens a window where it was last (Mac: windows keep their frame): its
    /// default size and place first (<paramref name="defaults"/>, which also
    /// sets its minimum), then the saved place if it's on a screen there is now.
    /// </summary>
    public static void Place(Window window, string name, Action defaults)
    {
        defaults();
        // Its size and place while it isn't maximized, as they change: what a maximized window goes back to.
        window.AppWindow.Changed += (sender, e) =>
        {
            if ((e.DidSizeChange || e.DidPositionChange) && sender.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Restored })
            {
                NormalBounds.AddOrUpdate(window, new Windows.Graphics.RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height));
            }
        };
        if (App.Current.Settings.WindowPlaces?.GetValueOrDefault(name) is not { } saved) return;
        var areas = Microsoft.UI.Windowing.DisplayArea.FindAll()
            .Select(d => new LLMTray.Core.Settings.WindowPlace.Area(d.WorkArea.X, d.WorkArea.Y, d.WorkArea.Width, d.WorkArea.Height)).ToList();
        var presenter = window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        // A small floor, not the window's minimum: that is in this screen's pixels, and the place may be on another scale.
        if (saved.On(areas, 320, 240) is not { } place) return;
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(place.X, place.Y, place.Width, place.Height));
        if (place.Maximized) presenter?.Maximize();
    }

    /// <summary>Each placed window's bounds while not maximized.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> NormalBoundsBox = [];

    static class NormalBounds
    {
        public static void AddOrUpdate(Window window, Windows.Graphics.RectInt32 bounds) => NormalBoundsBox.AddOrUpdate(window, bounds);

        public static bool TryGetValue(Window window, out Windows.Graphics.RectInt32 bounds)
        {
            if (NormalBoundsBox.TryGetValue(window, out var box) && box is Windows.Graphics.RectInt32 b)
            {
                bounds = b;
                return true;
            }
            bounds = default;
            return false;
        }
    }

    /// <summary>Notes where a window is, for <see cref="Place"/> next time (as it closes, or as the app quits).</summary>
    public static void RememberPlace(Window window, string name)
    {
        var app = App.Current;
        var state = (window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.State;
        // Minimized, its bounds are nowhere on the screen: the place it had before is kept.
        if (state == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized && !NormalBounds.TryGetValue(window, out _)) return;
        var maximized = state == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        var places = new Dictionary<string, LLMTray.Core.Settings.WindowPlace>(app.Settings.WindowPlaces ?? new Dictionary<string, LLMTray.Core.Settings.WindowPlace>());
        // Maximized, its bounds are the screen's: the size it had before it was maximized is what's kept.
        var bounds = (maximized || state == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized) && NormalBounds.TryGetValue(window, out var normal) ? normal
            : new Windows.Graphics.RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y, window.AppWindow.Size.Width, window.AppWindow.Size.Height);
        var place = new LLMTray.Core.Settings.WindowPlace(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized);
        if (places.GetValueOrDefault(name) == place) return;
        places[name] = place;
        app.UpdateSettings(app.Settings with { WindowPlaces = places });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}
