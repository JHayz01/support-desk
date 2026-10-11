using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SupportDesk.Desktop;

public sealed class PasteToast : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public PasteToast(string title, string hint = "Паста вставлена")
    {
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = hint, Foreground = new SolidColorBrush(Color.FromRgb(139, 208, 167)), FontSize = 12, Margin = new Thickness(0, 0, 0, 5) });
        text.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis });
        Content = new Border { Background = new SolidColorBrush(Color.FromRgb(29, 33, 48)), BorderBrush = new SolidColorBrush(Color.FromRgb(66, 78, 94)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(17, 13, 17, 13), Child = text };
        SourceInitialized += (_, _) => { var handle = new WindowInteropHelper(this).Handle; SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x20 | 0x80); };
        Loaded += (_, _) => { var area = SystemParameters.WorkArea; Left = area.Right - Width - 20; Top = area.Bottom - ActualHeight - 20; timer.Start(); };
        timer.Tick += (_, _) => Close();
        Closed += (_, _) => timer.Stop();
    }
}
