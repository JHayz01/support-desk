using System.Runtime.InteropServices;
using System.Windows.Interop;
namespace SupportDesk.Desktop;

public sealed class Hotkeys : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    readonly HwndSource source; readonly Dictionary<int, Action> actions = [];
    (string Gesture, Action Action)[] configured = [];
    bool suspended;
    public string Warning { get; private set; } = "";
    public Hotkeys(Window window)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())!;
        source.AddHook(Hook);
    }
    public static (uint Mod, uint Key) Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Укажите сочетание.");
        uint mod = 0;
        foreach (string p in parts[..^1])
            mod |= p.ToLowerInvariant() switch
            {
                "ctrl" => 2u,
                "alt" => 1u,
                "shift" => 4u,
                "win" => 8u,
                _ => throw new ArgumentException("Неизвестная клавиша: " + p)
            };
        var name = parts[^1];
        if (name.Length == 1 && char.IsDigit(name[0]))
            name = "D" + name;
        if (!Enum.TryParse<Key>(name, true, out var key) || key is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin || KeyInterop.VirtualKeyFromKey(key) == 0)
            throw new ArgumentException("Нажмите клавишу или сочетание с Ctrl, Alt, Shift, Win.");
        return (mod, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    public static string Format(Key key, ModifierKeys modifiers)
    {
        string name = key >= Key.D0 && key <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString();
        string value = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "") + name;
        Parse(value);
        return value;
    }
    public void Suspend()
    {
        suspended = true;
        Clear();
    }
    public void Resume()
    {
        if (!suspended)
            return;
        suspended = false;
        Apply(configured, []);
    }
    void Clear()
    {
        foreach (var id in actions.Keys)
            UnregisterHotKey(source.Handle, id);
        actions.Clear();
    }
    public void Set(IEnumerable<(string Gesture, Action Action)> entries, IEnumerable<string>? requiredGestures = null)
    {
        var next = entries.ToArray();
        var parsed = next.Select(e => Parse(e.Gesture)).ToArray();
        if (parsed.Distinct().Count() != parsed.Length)
            throw new ArgumentException("Бинд совпадает с сочетанием панели или списка биндов. Выберите другую клавишу.");
        var required = requiredGestures?.Select(Parse).ToHashSet() ?? parsed.ToHashSet();
        var previous = configured;
        try
        {
            if (!suspended)
                Apply(next, required);
            configured = next;
        }
        catch { Apply(previous, []); throw; }
    }
    void Apply((string Gesture, Action Action)[] entries, HashSet<(uint Mod, uint Key)> required)
    {
        var parsed = entries.Select(e => (Keys: Parse(e.Gesture), e.Action, e.Gesture)).ToArray();
        Clear();
        var unavailable = new List<string>();
        int id = 100;
        foreach (var e in parsed)
        {
            if (!RegisterHotKey(source.Handle, id, e.Keys.Mod | 0x4000, e.Keys.Key))
            {
                if (required.Contains(e.Keys))
                    throw new InvalidOperationException("Сочетание занято другим приложением: " + e.Gesture + ". Выберите другое или закройте приложение, использующее его.");
                unavailable.Add(e.Gesture);
                continue;
            }
            actions[id++] = e.Action;
        }
        Warning = unavailable.Count == 0 ? "" : "Заняты сочетания: " + string.Join(", ", unavailable) + ". Измените их в настройках или закройте прежнюю версию приложения.";
    }
    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x312 && actions.TryGetValue(w.ToInt32(), out var action))
        {
            try
            {
                action();
            }
            catch (Exception) { }
            handled = true;
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        Clear();
        source.RemoveHook(Hook);
    }
}
