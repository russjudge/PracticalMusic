using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PracticalMusic.Core.Timing;

namespace PracticalMusic.App.Converters;

public static class AppConverters
{
    /// <summary>Milliseconds → "m:ss.t".</summary>
    public static readonly IValueConverter TimeMs = new FuncValueConverter<double, string>(ms => TimingEngine.FormatTime(ms));

    /// <summary>0-based page index → 1-based page number string.</summary>
    public static readonly IValueConverter PageNumber = new FuncValueConverter<int, string>(i => (i + 1).ToString(CultureInfo.InvariantCulture));

    public static readonly IValueConverter IsNotNull = new FuncValueConverter<object?, bool>(o => o != null);

    public static readonly IValueConverter IsNull = new FuncValueConverter<object?, bool>(o => o == null);

    public static readonly IValueConverter IsNotEmpty = new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));

    /// <summary>True → translucent accent background for pages that belong to the selected song.</summary>
    public static readonly IValueConverter SelectedSongBrush = new FuncValueConverter<bool, IBrush?>(selected =>
        selected ? new SolidColorBrush(Color.FromArgb(50, 0, 120, 215)) : Brushes.Transparent);

    /// <summary>True (missing file) → red text, otherwise gray.</summary>
    public static readonly IValueConverter MissingBrush = new FuncValueConverter<bool, IBrush?>(missing =>
        missing ? Brushes.OrangeRed : Brushes.Gray);

    public static readonly IValueConverter BoldIfTrue = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);

    /// <summary>Step state (0 pending, 1 current, 2 done) → chip background.</summary>
    public static readonly IValueConverter StepBrush = new FuncValueConverter<int, IBrush?>(state => state switch
    {
        1 => new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)),
        2 => new SolidColorBrush(Color.FromArgb(90, 48, 160, 48)),
        _ => new SolidColorBrush(Color.FromArgb(48, 128, 128, 128))
    });

    /// <summary>Step state → chip text colour (white on the current step, theme default otherwise).</summary>
    public static readonly IValueConverter StepForeground = new FuncValueConverter<int, object?>(state =>
        state == 1 ? Brushes.White : Avalonia.AvaloniaProperty.UnsetValue);

    public static readonly IValueConverter ItalicIfTrue = new FuncValueConverter<bool, FontStyle>(b => b ? FontStyle.Italic : FontStyle.Normal);
    /// <summary>Page margin in the Player: none in full-display mode, a hairline otherwise.</summary>
    public static readonly IValueConverter PageMargin = new FuncValueConverter<bool, Avalonia.Thickness>(fill => fill ? new Avalonia.Thickness(0) : new Avalonia.Thickness(2));

    public static readonly IValueConverter IsNonZero = new FuncValueConverter<int, bool>(i => i != 0);

    /// <summary>True → a star column, false → zero width (collapses the second page).</summary>
    public static readonly IValueConverter StarIfTrue = new FuncValueConverter<bool, Avalonia.Controls.GridLength>(b =>
        b ? new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star) : new Avalonia.Controls.GridLength(0));

    public static readonly IValueConverter Bytes = new FuncValueConverter<long, string>(b =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B");
}
