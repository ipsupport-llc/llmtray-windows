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

    public static readonly Brush Green = new SolidColorBrush(ColorHelper.FromArgb(255, 0x2E, 0xA0, 0x43));
    public static readonly Brush Orange = new SolidColorBrush(ColorHelper.FromArgb(255, 0xD8, 0x7A, 0x00));
    public static readonly Brush Red = new SolidColorBrush(ColorHelper.FromArgb(255, 0xD1, 0x34, 0x38));
    public static readonly Brush Gray = new SolidColorBrush(Colors.Gray);

    public static TextBlock Section(string title) => new()
    {
        Text = title,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(4, 16, 0, 2),
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
        var grid = new Grid { Padding = new Thickness(16, 11, 16, 11), ColumnSpacing = 16, MinHeight = 48 };
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

    public static TextBlock Value(string text, bool mono = false) => new()
    {
        Text = text,
        Foreground = Secondary,
        FontFamily = mono ? Mono : FontFamily.XamlAutoFontFamily,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 420,
        IsTextSelectionEnabled = true,
    };

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
        button.Click += (_, _) => click();
        return button;
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
