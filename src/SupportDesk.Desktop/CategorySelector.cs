namespace SupportDesk.Desktop;

public sealed class CategorySelector : StackPanel
{
    readonly ComboBox categories;
    readonly TextBox custom;
    readonly ComboBoxItem customOption = new() { Content = "Своя категория…" };
    public string Value => ReferenceEquals(categories.SelectedItem, customOption)
        ? custom.Text.Trim() : (categories.SelectedItem as string ?? "Другое");

    public CategorySelector(IEnumerable<string> existing, string initial)
    {
        Children.Add(new TextBlock { Text = "Категория", Margin = new Thickness(4, 9, 4, 3) });
        categories = new ComboBox { IsTextSearchEnabled = true, MaxDropDownHeight = 280 };
        var names = existing.Where(c => c != "Все категории" && !string.IsNullOrWhiteSpace(c)).Append("Другое");
        if (!string.IsNullOrWhiteSpace(initial)) names = names.Append(initial);
        foreach (var name in names.Distinct().Order()) categories.Items.Add(name);
        categories.Items.Add(customOption);
        Children.Add(categories);
        var customForm = new StackPanel { Visibility = Visibility.Collapsed };
        customForm.Children.Add(new TextBlock { Text = "Название своей категории", Margin = new Thickness(4, 9, 4, 3) });
        custom = new TextBox { MaxLength = 500 };
        customForm.Children.Add(custom);
        Children.Add(customForm);
        categories.SelectionChanged += (_, _) =>
        {
            bool own = ReferenceEquals(categories.SelectedItem, customOption);
            customForm.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
            if (own) Dispatcher.BeginInvoke(() => custom.Focus());
        };
        categories.SelectedItem = string.IsNullOrWhiteSpace(initial) ? "Другое" : initial;
    }
}
