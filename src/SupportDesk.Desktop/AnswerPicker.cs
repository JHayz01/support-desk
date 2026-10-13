using System.Windows.Media;

namespace SupportDesk.Desktop;

public sealed class AnswerPicker : Window
{
    public event Action<Answer>? Chosen;
    public event Func<Answer, Task<bool>>? DeleteRequested;
    readonly ListBox list;
    bool choosing;
    public AnswerPicker(IReadOnlyList<Answer> answers, string gesture, Func<Answer, string>? badge = null, string? heading = null)
    {
        Title = "Ответы · " + gesture;
        Width = 540;
        Height = 440;
        MinWidth = 380;
        MinHeight = 280;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        Topmost = true;
        Background = ThemePalette.Brush("#151720");
        Foreground = ThemePalette.Brush("#E9EAF0");
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        grid.RowDefinitions.Add(new()
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        grid.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        grid.Children.Add(new TextBlock { Text = heading ?? "Ответы на " + gesture, FontSize = 20, Margin = new Thickness(4, 0, 0, 14) });
        list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ThemePalette.Brush("#E9EAF0") };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        foreach (var answer in answers)
        {
            var card = new StackPanel();
            if (badge is not null)
                card.Children.Add(new TextBlock { Text = badge(answer), Foreground = ThemePalette.Brush("#B9AEF5"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
            card.Children.Add(new TextBlock { Text = answer.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(answer.Notes)) card.Children.Add(new TextBlock { Text=answer.Notes, TextWrapping=TextWrapping.Wrap, Foreground=ThemePalette.Brush("#B9AEF5"), Margin=new Thickness(0,6,0,0) });
            card.Children.Add(new TextBlock { Text = answer.Preview, Foreground = ThemePalette.Brush("#ACB2C7"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7) });
            card.Children.Add(new TextBlock { Text = answer.Category, Foreground = ThemePalette.Brush("#A99CDA") });
            list.Items.Add(new ListBoxItem { Content = card, Tag = answer });
        }
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        list.SelectedIndex = 0;
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var insert = new Button { Content = "Вставить · Enter", MinWidth = 150 };
        DockPanel.SetDock(insert, Dock.Right);
        footer.Children.Add(insert);
        var delete = new Button { Content = "Удалить из базы", MinWidth = 130, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(delete, Dock.Right);
        footer.Children.Add(delete);
        footer.Children.Add(new TextBlock { Text = "↑ ↓ · Esc", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightSlateGray });
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        Content = grid;
        insert.Click += (_, _) => Choose();
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: Answer answer } || DeleteRequested is null)
                return;
            if (MessageBox.Show(this, $"Удалить «{answer.Title}» из общей базы? Если у пасты есть другие варианты ответа, они тоже будут удалены.", "Удаление пасты", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            delete.IsEnabled = false;
            try
            {
                if (await DeleteRequested(answer))
                {
                    foreach (var item in list.Items.OfType<ListBoxItem>().Where(item => item.Tag is Answer a && a.Id == answer.Id).ToArray())
                        list.Items.Remove(item);
                    if (list.Items.Count == 0) Close();
                    else list.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex is AccessDeniedException ? "Доступ истёк. Войдите заново." : ex.Message, "Не удалось удалить пасту", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { delete.IsEnabled = true; }
        };
        list.MouseDoubleClick += (_, _) => Choose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } else if (e.Key == Key.Enter && !delete.IsKeyboardFocusWithin) { Choose(); e.Handled = true; } };
        Loaded += (_, _) => { list.Focus(); ((ListBoxItem)list.Items[0]).Focus(); };
    }
    void Choose()
    {
        if (!choosing && list.SelectedItem is ListBoxItem { Tag: Answer answer })
        {
            choosing = true;
            Chosen?.Invoke(answer);
        }
    }
}
