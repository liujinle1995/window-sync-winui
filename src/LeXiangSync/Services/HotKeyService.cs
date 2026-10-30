using LeXiangSync.Native;

namespace LeXiangSync.Services;

/// <summary>全局热键（默认 Ctrl+Alt+S = 启停同步），组合键可在设置页修改。</summary>
public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 0x5348;

    public const uint DefaultModifiers = Win32.MOD_CONTROL | Win32.MOD_ALT;
    public const uint DefaultKey = Win32.VK_S;

    private readonly HiddenMessageWindow _window;
    private uint _modifiers = DefaultModifiers;
    private uint _key = DefaultKey;
    private bool _registered;

    public event EventHandler? HotKeyPressed;

    public HotKeyService(HiddenMessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    public bool IsRegistered => _registered;

    /// <summary>当前生效的组合（供界面回显）。</summary>
    public string CurrentText => Describe(_modifiers, _key);

    /// <summary>
    /// 按给定组合重新注册。enabled=false 时只注销不注册。
    /// 组合已被其它程序占用时抛异常，由调用方决定回退策略。
    /// </summary>
    public void Apply(uint modifiers, uint key, bool enabled)
    {
        Unregister();

        _modifiers = modifiers;
        _key = key;
        if (!enabled) return;

        if (!Register())
            throw new InvalidOperationException(
                $"注册全局热键 {Describe(modifiers, key)} 失败，可能已被其它程序占用");
    }

    private bool Register()
    {
        if (_registered) return true;
        _registered = Win32.RegisterHotKey(_window.Handle, HotKeyId, _modifiers | Win32.MOD_NOREPEAT, _key);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(_window.Handle, HotKeyId);
        _registered = false;
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_HOTKEY || wParam.ToInt32() != HotKeyId) return;
        HotKeyPressed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把「修饰键掩码 + 虚拟键码」翻译成 Ctrl + Alt + S 这样的文本。</summary>
    public static string Describe(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint key) => key switch
    {
        >= 0x41 and <= 0x5A => ((char)key).ToString(),   // A-Z
        >= 0x30 and <= 0x39 => ((char)key).ToString(),   // 0-9
        >= 0x70 and <= 0x7B => $"F{key - 0x6F}",         // F1-F12
        _ => $"0x{key:X2}",
    };

    public void Dispose()
    {
        Unregister();
        _window.MessageReceived -= OnMessage;
        GC.SuppressFinalize(this);
    }
}
