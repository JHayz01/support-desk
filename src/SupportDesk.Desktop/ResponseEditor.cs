namespace SupportDesk.Desktop;
public sealed class ResponseEditor : StackPanel
{
    readonly List<ReplyOption> rows;
    readonly ComboBox options = new() { DisplayMemberPath="DisplayTitle" };
    readonly TextBox label = new(), condition = new(), step = new(), body = new() { AcceptsReturn=true, TextWrapping=TextWrapping.Wrap, Height=190, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    ReplyOption? active;
    bool changing;
    public ResponseEditor(Answer answer)
    {
        rows = answer.Options.Select(o => o with {}).ToList();
        AddLabel("Ответы на проблему · порядок можно менять"); Children.Add(options);
        var actions = new WrapPanel(); Children.Add(actions);
        void Button(string text, Action action) { var button = new Button { Content=text }; button.Click += (_,_) => { try { Commit(); action(); } catch (Exception ex) { MessageBox.Show(ex.Message); } }; actions.Children.Add(button); }
        Button("Добавить вариант", () => { var row = new ReplyOption(); rows.Add(row); Refresh(row); });
        Button("Удалить вариант", () => { if(rows.Count>1 && active is not null) { rows.Remove(active); Refresh(rows[0]); } });
        Button("Выше", () => Move(-1)); Button("Ниже", () => Move(1));
        AddLabel("Название варианта / шага"); Children.Add(label);
        AddLabel("Когда применять · это пояснение не копируется"); Children.Add(condition);
        AddLabel("Номер шага: 0 — самостоятельный вариант, 1–99 — шаг последовательности"); Children.Add(step);
        AddLabel("Текст ответа · копируется без изменений"); Children.Add(body);
        options.SelectionChanged += (_,_) => { if(changing || options.SelectedItem is not ReplyOption row) return; try { Commit(); Show(row); } catch(Exception ex) { MessageBox.Show(ex.Message); options.SelectedItem=active; } };
        Refresh(rows[0]);
    }
    void AddLabel(string text) => Children.Add(new TextBlock { Text=text, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(4,9,4,3) });
    void Commit()
    {
        if(active is null) return;
        if(!int.TryParse(step.Text, out int n) || n<0 || n>99) throw new ArgumentException("Номер шага должен быть от 0 до 99.");
        active.Title=label.Text.Trim(); active.When=condition.Text; active.Body=body.Text; active.Step=n;
    }
    void Show(ReplyOption row) { active=row; label.Text=row.Title; condition.Text=row.When; step.Text=row.Step.ToString(); body.Text=row.Body; }
    void Refresh(ReplyOption row) { changing=true; options.ItemsSource=null; options.ItemsSource=rows.ToArray(); options.SelectedIndex=rows.IndexOf(row); Show(row); changing=false; }
    void Move(int delta) { if(active is null)return; int at=rows.IndexOf(active), next=at+delta; if(next<0||next>=rows.Count)return; (rows[at],rows[next])=(rows[next],rows[at]); Refresh(active); }
    public List<ReplyOption> GetOptions() { Commit(); if(rows.Any(o=>string.IsNullOrWhiteSpace(o.Body))) throw new ArgumentException("Заполните текст каждого варианта ответа."); return rows.Select(o=>o with {}).ToList(); }
}
