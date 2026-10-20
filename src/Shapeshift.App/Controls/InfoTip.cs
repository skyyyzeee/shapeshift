using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Shapeshift.App.Controls;

/// <summary>
/// Значок «i» рядом с полем, где не сразу понятно, что вводить: при наведении (или фокусе с клавиатуры)
/// показывает пояснение с примерами. Текст — из ресурсов: <c>x:Uid="…Tip"</c> и ключ <c>…Tip.Text</c>.
/// Значок — Info из системного шрифта значков (Segoe Fluent Icons / Segoe MDL2 Assets).
/// </summary>
public sealed partial class InfoTip : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InfoTip), new PropertyMetadata(null, (d, e) => ((InfoTip)d).OnTextChanged((string?)e.NewValue)));

    /// <summary>Строки разделяются переводом строки; каждая — отдельным абзацем.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    readonly TextBlock _tipText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };

    public InfoTip()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        VerticalAlignment = VerticalAlignment.Center;
        Content = new FontIcon
        {
            Glyph = "\uE946",
            FontSize = 14,
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        ToolTipService.SetToolTip(this, new ToolTip { Content = _tipText, MaxWidth = 420 });
    }

    void OnTextChanged(string? text)
    {
        _tipText.Text = text ?? "";
        AutomationProperties.SetHelpText(this, text ?? "");
        AutomationProperties.SetName(this, Services.Text.Get("InfoTipName"));
    }
}
