using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Googlook.ViewModels;

/// <summary>Small, reusable converters so the XAML stays declarative.</summary>
public static class Conv
{
    public static readonly IValueConverter IntToBool =
        new FuncValueConverter<int, bool>(i => i > 0);

    public static readonly IValueConverter UnreadToWeight =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);

    public static readonly IValueConverter StarBrush =
        new FuncValueConverter<bool, IBrush>(b =>
            b ? new SolidColorBrush(Color.Parse("#F4B400"))   // Gmail star yellow
              : new SolidColorBrush(Color.Parse("#DADCE0"))); // muted grey
}
