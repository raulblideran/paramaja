using Avalonia.Controls;
using Avalonia.Interactivity;
using Paramaja.App.ViewModels;

namespace Paramaja.App.Views;

public partial class OpenLinkDialog : Window
{
    public OpenLinkDialog() : this(new OpenLinkDialogViewModel()) { }

    public OpenLinkDialog(OpenLinkDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Opened += (_, _) => Input.Focus();
    }

    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OpenLinkDialogViewModel { IsValid: true } vm) Close(vm.Normalized);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
