using System.Runtime.InteropServices;

namespace SupportDesk.Desktop;

public static class PasteService
{
    internal static readonly UIntPtr InputMarker = new(0x43535038);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type; public InputData Data;
    }
    [StructLayout(LayoutKind.Explicit)]
    struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput
    {
        public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra;
    }
    static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Extra = InputMarker, Flags = up ? 2u : 0u } } };
    static bool ActivateTarget(IntPtr target)
    {
        if (GetForegroundWindow() == target || SetForegroundWindow(target))
            return true;
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _), current = GetCurrentThreadId();
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try
        {
            BringWindowToTop(target);
            return SetForegroundWindow(target) || GetForegroundWindow() == target;
        }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    public static async Task<bool> PasteAsync(IntPtr target, string text, Func<bool> allowed, uint triggerKey = 0, int eraseCharacters = 0, bool keepFocus = false)
    {
        // Wait for the physical shortcut to be released before synthesizing Ctrl+V.
        int[] keys = [0x01, 0x10, 0x11, 0x12, 0x5B, 0x5C, (int)triggerKey];
        for (int i = 0; i < 100 && keys.Any(k => k != 0 && (GetAsyncKeyState(k) & 0x8000) != 0); i++)
            await Task.Delay(20);
        if (!allowed())
            return false;
        if (keys.Any(k => k != 0 && (GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("Отпустите клавиши и повторите вставку.");
        if (target == IntPtr.Zero || !IsWindow(target))
            throw new InvalidOperationException("Окно для вставки уже закрыто.");
        if (keepFocus && GetForegroundWindow() != target) throw new InvalidOperationException("Окно изменилось. Введите команду снова.");
        if (!keepFocus && !ActivateTarget(target))
            throw new InvalidOperationException("Не удалось вернуть фокус. Ответ можно скопировать из панели.");
        await Task.Delay(80);
        if (!allowed())
            return false;
        if (GetForegroundWindow() != target)
            throw new InvalidOperationException("Фокус окна изменился. Повторите вставку в нужном поле.");
        if (text.Length > 0) Clipboard.SetText(text);
        var inputs = Enumerable.Range(0, Math.Clamp(eraseCharacters, 0, 50)).SelectMany(_ => new[] { Key(8), Key(8, true) }).Concat(text.Length > 0 ? [Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true)] : Array.Empty<Input>()).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows заблокировала вставку. Проверьте, что приложения запущены с одинаковыми правами.");
        // Let the target consume the replacement before a picker can take focus.
        if (inputs.Length > 0) await Task.Delay(100);
        return true;
    }
}
