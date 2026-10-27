using Avalonia.Controls;
using Avalonia.Interactivity;
using Paramaja.App.ViewModels;

namespace Paramaja.App.Views;

public partial class DownloadDialog : Window
{
    public DownloadDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            Input.Focus();
            // Opened with a link already in the box: look up its qualities straight away.
            if (DataContext is DownloadDialogViewModel vm && vm.CheckCommand.CanExecute(null)) vm.CheckCommand.Execute(null);
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
