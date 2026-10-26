using Avalonia.Markup.Xaml;
using Paramaja.App.ViewModels;

namespace Paramaja.App.Views;

public partial class ControlsBar : OverlayWindow
{
    public const double BarHeight = 118;

    public ControlsBar()
    {
        InitializeComponent();
        Seek.SeekRequested += t => (DataContext as MainViewModel)?.SeekTo(t);
    }
}
