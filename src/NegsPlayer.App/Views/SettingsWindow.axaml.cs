using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using NegsPlayer.Core.Input;

namespace NegsPlayer.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
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
