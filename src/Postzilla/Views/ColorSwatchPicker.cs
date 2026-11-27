using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Postzilla.Views;

/// <summary>A row of preset colour swatches, a "none" swatch and an optional custom colour picker.</summary>
public sealed class ColorSwatchPicker : StackPanel
{
    private readonly StackPanel _swatches = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Button _noneSwatch;
    private readonly ColorPicker _picker = new()
    {
        IsAlphaEnabled = false,
        IsMoreButtonVisible = false,
        IsColorSliderVisible = true,
        IsColorChannelTextInputVisible = false,
        IsHexInputVisible = true,
        ColorSpectrumShape = ColorSpectrumShape.Ring,
        Visibility = Visibility.Collapsed,
    };
    private string? _color;
    private bool _settingPicker;

    public ColorSwatchPicker(string noneTooltip)
    {
        Spacing = 8;
        _noneSwatch = Swatch(null);
        _noneSwatch.Content = new FontIcon { Glyph = "\uE711", FontSize = 12 };
        ToolTipService.SetToolTip(_noneSwatch, noneTooltip);
        _swatches.Children.Add(_noneSwatch);
        foreach (var hex in ColorPalette.Presets)
        {
            var swatch = Swatch(hex);
            swatch.Background = ColorPalette.Brush(hex);
            ToolTipService.SetToolTip(swatch, hex);
            _swatches.Children.Add(swatch);
        }
        var custom = new HyperlinkButton { Content = "Custom colour…", Padding = new Thickness(4, 0, 4, 0) };
        custom.Click += (_, _) => _picker.Visibility = _picker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _swatches.Children.Add(custom);
        _picker.ColorChanged += (_, args) =>
        {
            if (_settingPicker) return;
            _color = ColorPalette.ToHex(args.NewColor);
            UpdateSwatches();
        };
        Children.Add(_swatches);
        Children.Add(_picker);
        UpdateSwatches();
    }

    /// <summary>The selected #RRGGBB, or null for none.</summary>
    public string? Color
    {
        get => _color;
        set
        {
            _color = ColorPalette.TryParse(value) is { } c ? ColorPalette.ToHex(c) : null;
            if (ColorPalette.TryParse(_color) is { } parsed)
            {
                _settingPicker = true;
                _picker.Color = parsed;
                _settingPicker = false;
            }
            UpdateSwatches();
        }
    }

    private Button Swatch(string? hex)
    {
        var button = new Button
        {
            Width = 28, Height = 28, Padding = new Thickness(0), Tag = hex,
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(14),
        };
        button.Click += (_, _) => Color = hex;
        return button;
    }

    private void UpdateSwatches()
    {
        foreach (var b in _swatches.Children.OfType<Button>())
        {
            var selected = string.Equals(_color, b.Tag as string, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = new SolidColorBrush(selected ? Colors.White : Colors.Transparent);
        }
    }
}
