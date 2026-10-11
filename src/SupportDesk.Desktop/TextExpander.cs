using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SupportDesk.Desktop;

public sealed class TextExpander : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder buffer, int length, uint flags, IntPtr layout);
    [StructLayout(LayoutKind.Sequential)] struct KeyboardEvent { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    readonly HookProc callback;
    readonly Dispatcher dispatcher;
    readonly Func<bool> enabled;
    readonly Func<string, bool> exists;
    readonly Action<string, IntPtr, Func<bool>> expand;
    readonly CommandMatcher matcher = new();
    IntPtr hook;
    IntPtr mouseHook;
    readonly Thread hookThread;
    Dispatcher? hookDispatcher;
    readonly HookProc mouseCallback;
    int generation;
    readonly Func<IntPtr, bool>? windowAllowed;
    IntPtr target;
    DateTime last;
    public TextExpander(Dispatcher dispatcher, Func<bool> enabled, Func<string, bool> exists, Action<string, IntPtr, Func<bool>> expand)
        : this(dispatcher, enabled, exists, expand, null) { }
    internal TextExpander(Dispatcher dispatcher, Func<bool> enabled, Func<string, bool> exists, Action<string, IntPtr, Func<bool>> expand, Func<IntPtr, bool>? windowAllowed)
    {
        this.windowAllowed = windowAllowed;
        this.dispatcher = dispatcher; this.enabled = enabled; this.exists = exists; this.expand = expand;
        callback = Hook;
        mouseCallback = (code, message, data) => { if (code >= 0 && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B or 0x20A) Reset(); return CallNextHookEx(mouseHook, code, message, data); };
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        hookThread = new Thread(() => {
            hookDispatcher = Dispatcher.CurrentDispatcher;
            try {
                hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new InvalidOperationException("Не удалось включить замену команд: " + Marshal.GetLastWin32Error());
                mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
                if (mouseHook == IntPtr.Zero) throw new InvalidOperationException("Не удалось включить отслеживание перемещения курсора.");
            } catch (Exception ex) { failure = ex; }
            finally { ready.Set(); }
            if (failure is null) Dispatcher.Run();
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        }) { IsBackground = true, Name = "Support commands" };
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start(); ready.Wait();
        if (failure is not null) throw failure;
    }
    IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        var input = Marshal.PtrToStructure<KeyboardEvent>(data);
        if (input.Extra == PasteService.InputMarker) { matcher.Reset(); return CallNextHookEx(hook, code, message, data); }
        if (message.ToInt64() is not (0x100 or 0x104)) return CallNextHookEx(hook, code, message, data);
        Interlocked.Increment(ref generation);
        try
        {
            var foreground = PasteService.GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint process);
            if (!enabled() || (windowAllowed is null ? process == Environment.ProcessId : !windowAllowed(foreground))) { matcher.Reset(); return CallNextHookEx(hook, code, message, data); }
            if (foreground != target || DateTime.UtcNow - last > TimeSpan.FromSeconds(8) || (GetAsyncKeyState(1) & 0x8000) != 0) matcher.Reset();
            target = foreground; last = DateTime.UtcNow;
            // Layout-switch modifiers do not insert text: preserve the candidate.
            if (input.Key is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
                return CallNextHookEx(hook, code, message, data);
            if (input.Key == 0x20 && ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0))
                return CallNextHookEx(hook, code, message, data);
            if ((GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) { matcher.Reset(); return CallNextHookEx(hook, code, message, data); }
            if (input.Key is 0x10 or 0xA0 or 0xA1) return CallNextHookEx(hook, code, message, data);
            if (input.Key == 8) matcher.Backspace();
            else
            {
                var state = new byte[256];
                state[input.Key & 255] = 0x80;
                state[0x10] = (byte)((GetAsyncKeyState(0x10) & 0x8000) != 0 ? 0x80 : 0);
                state[0x14] = (byte)(GetKeyState(0x14) & 1);
                var buffer = new StringBuilder(8);
                int length = ToUnicodeEx(input.Key, input.Scan, state, buffer, buffer.Capacity, 4, GetKeyboardLayout(GetWindowThreadProcessId(foreground, out _)));
                if (length > 0)
                {
                    var command = matcher.Feed(buffer.ToString(0, length), exists);
                    if (command is not null)
                    {
                        int captured = Volatile.Read(ref generation);
                        dispatcher.BeginInvoke(() => expand(command, foreground, () => Volatile.Read(ref generation) == captured && enabled()));
                        return new IntPtr(1); // Consume only the confirming space.
                    }
                }
                else matcher.Reset();
            }
        }
        catch { matcher.Reset(); }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Reset() { matcher.Reset(); Interlocked.Increment(ref generation); }
    public void Dispose() { hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send); }
}
