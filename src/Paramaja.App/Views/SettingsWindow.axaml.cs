using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Paramaja.App.Services;
using Paramaja.Core.Input;

namespace Paramaja.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Title = $"Settings — {AppInfo.Name} {AppInfo.Display}";
        ShortcutList.ItemTemplate = new FuncDataTemplate<(string Keys, PlayerAction Action)>((item, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new(0, 3) };
            grid.Children.Add(new TextBlock { Text = item.Keys, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            var label = new TextBlock { Text = Humanize(item.Action), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            return grid;
        });
    }

    private async void OnCopyVersion(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Clipboard is { } c) await c.SetTextAsync($"{AppInfo.Name} {AppInfo.Display}");
    }

    internal static string Humanize(PlayerAction action)
    {
        var s = action.ToString();
        var chars = new List<char>();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) chars.Add(' ');
            chars.Add(i > 0 && char.IsUpper(s[i]) ? char.ToLowerInvariant(s[i]) : s[i]);
        }
        return new string(chars.ToArray());
    }
}
