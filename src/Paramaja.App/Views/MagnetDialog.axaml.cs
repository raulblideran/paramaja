using Avalonia.Controls;
using Avalonia.Interactivity;
using Paramaja.App.ViewModels;

namespace Paramaja.App.Views;

public partial class MagnetDialog : Window
{
    public MagnetDialog() : this(new MagnetDialogViewModel()) { }

    public MagnetDialog(MagnetDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Opened += (_, _) => Input.Focus();
    }

    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MagnetDialogViewModel { IsValid: true } vm) Close(vm.Normalized);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
