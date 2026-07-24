using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Googlook.Controls;

/// <summary>The Googlook envelope mark, used in the top bar, lock screen, and splash.</summary>
public partial class LogoMark : UserControl
{
    public LogoMark() => AvaloniaXamlLoader.Load(this);
}
