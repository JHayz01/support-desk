using System.Collections.ObjectModel;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
namespace SupportDesk.Desktop;
public sealed class CategorySearchBox : ComboBox
{
    ListCollectionView? view;
    Func<string>? current;
    Action<string>? choose;
    string filter = "";
    bool updating;
    public CategorySearchBox()
    {
        IsEditable = true; IsTextSearchEnabled = false; StaysOpenOnEdit = true;
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Changed));
        SelectionChanged += (_, _) => {
            if (updating || SelectedItem is not string category) return;
            choose?.Invoke(category);
            filter = "";
            Dispatcher.BeginInvoke(() => { updating = true; view?.Refresh(); Text = current?.Invoke() ?? category; updating = false; });
        };
        DropDownClosed += (_, _) => { if (!updating && SelectedItem is string category) choose?.Invoke(category); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Items.Count > 0) { if (SelectedItem is null) SelectedIndex = 0; if (SelectedItem is string category) choose?.Invoke(category); IsDropDownOpen = false; e.Handled = true; } };
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => {
            if (IsKeyboardFocusWithin || IsDropDownOpen) return;
            updating = true; filter = ""; view?.Refresh(); SelectedItem = current?.Invoke(); Text = current?.Invoke() ?? ""; updating = false;
        });
    }
    public void Configure(ObservableCollection<string> categories, Func<string> current, Action<string> choose)
    {
        this.current = current; this.choose = choose;
        view = new ListCollectionView(categories) { Filter = value => SearchIndex.Normalize((string)value).Contains(filter, StringComparison.Ordinal) };
        updating = true; ItemsSource = view; SelectedItem = current(); Text = current(); updating = false;
    }
    public void RefreshSelection()
    {
        if (IsKeyboardFocusWithin) return;
        updating = true; filter = ""; view?.Refresh(); SelectedItem = current?.Invoke(); Text = current?.Invoke() ?? ""; updating = false;
    }
    void Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || !IsKeyboardFocusWithin || e.OriginalSource is not TextBox editor || view is null) return;
        if (editor.Text == SelectedItem as string) return;
        string typed = editor.Text; int caret = editor.CaretIndex;
        filter = SearchIndex.Normalize(typed.Trim());
        updating = true; view.Refresh(); SelectedIndex = -1; Text = typed; editor.Text = typed; editor.CaretIndex = Math.Min(caret, typed.Length); updating = false;
        IsDropDownOpen = true;
    }
}
