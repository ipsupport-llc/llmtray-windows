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
        }
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
        var toggle = new ToggleSwitch { IsOn = on };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
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
}
