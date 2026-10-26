using Avalonia.Markup.Xaml;
using NegsPlayer.App.ViewModels;

namespace NegsPlayer.App.Views;

public partial class ControlsBar : OverlayWindow
{
    public const double BarHeight = 118;

    public ControlsBar()
    {
        InitializeComponent();
        Seek.SeekRequested += t => (DataContext as MainViewModel)?.SeekTo(t);
    }
}
