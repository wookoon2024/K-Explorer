using System.Globalization;
using System.Windows.Input;

namespace WorkFileExplorer.App.Models;

public sealed record ShortcutDefinition(
    string Id,
    string Name,
    string Description,
    Key DefaultKey,
    ModifierKeys DefaultModifiers)
{
    public ShortcutGesture DefaultGesture => new(DefaultKey, DefaultModifiers);
}

public readonly record struct ShortcutGesture(Key Key, ModifierKeys Modifiers)
{
    public static readonly ShortcutGesture Unassigned = new(Key.None, ModifierKeys.None);

    // Persisted settings lists drop empty string values, so "unassigned" needs a
    // non-empty marker or it would silently revert to the default on restart.
    public const string UnassignedMarker = "-";

    public bool IsAssigned => Key != Key.None;

    public string ToPersistText() => IsAssigned ? ToText() : UnassignedMarker;

    public string ToText() => BuildText(FormatKey);

    public string ToDisplayText() => BuildText(FormatKeyName);

    private string BuildText(Func<Key, string> formatKey)
    {
        if (!IsAssigned)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        if ((Modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(formatKey(Key));
        return string.Join("+", parts);
    }

    // Persisted form: Key enum names so TryParse can read it back.
    private static string FormatKey(Key key) => key switch
    {
        Key.Escape => "Esc",
        Key.Return => "Enter",
        Key.Delete => "Delete",
        Key.Back => "Backspace",
        Key.Space => "Space",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        _ => key.ToString()
    };

    // User-facing form used by the settings grid and the shortcut editor.
    public static string FormatKeyName(Key key) => key switch
    {
        Key.None => "(없음)",
        Key.Escape => "Esc",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Multiply => "Num*",
        Key.Add => "Num+",
        Key.Subtract => "Num-",
        Key.Decimal => "Num.",
        Key.Divide => "Num/",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + ((int)(key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture),
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ => key.ToString()
    };

    public static bool TryParse(string? text, out ShortcutGesture gesture)
    {
        gesture = Unassigned;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || string.Equals(value, UnassignedMarker, StringComparison.Ordinal))
        {
            return true;
        }

        var modifiers = ModifierKeys.None;
        var keyPart = value;
        var separator = value.LastIndexOf('+');
        if (separator >= 0)
        {
            keyPart = value[(separator + 1)..];
            foreach (var token in value[..separator].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (token.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= ModifierKeys.Control;
                        break;
                    case "shift":
                        modifiers |= ModifierKeys.Shift;
                        break;
                    case "alt":
                        modifiers |= ModifierKeys.Alt;
                        break;
                    case "win":
                    case "windows":
                        modifiers |= ModifierKeys.Windows;
                        break;
                    default:
                        return false;
                }
            }
        }

        if (!TryParseKey(keyPart, out var key))
        {
            return false;
        }

        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (string.Equals(text, "Esc", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Escape;
            return true;
        }

        if (string.Equals(text, "Enter", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Return;
            return true;
        }

        if (string.Equals(text, "Backspace", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Back;
            return true;
        }

        try
        {
            if (new KeyConverter().ConvertFromString(null, CultureInfo.InvariantCulture, text) is Key parsed &&
                parsed != Key.None)
            {
                key = parsed;
                return true;
            }
        }
        catch
        {
            // fall through: unknown key text
        }

        return false;
    }
}

public static class ShortcutInput
{
    // WPF reports Key.System (with SystemKey holding the real key) for Alt combinations.
    public static Key ResolveKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}

public static class ShortcutCatalog
{
    public const string Open = "open";
    public const string Rename = "rename";
    public const string EditWithExternalEditor = "editWithExternalEditor";
    public const string Delete = "delete";
    public const string NewFolder = "newFolder";
    public const string NewFile = "newFile";
    public const string Search = "search";
    public const string Properties = "properties";
    public const string AddFavorite = "addFavorite";
    public const string Copy = "copy";
    public const string Cut = "cut";
    public const string Paste = "paste";
    public const string SelectAll = "selectAll";
    public const string SelectNone = "selectNone";
    public const string GoBack = "goBack";
    public const string GoForward = "goForward";
    public const string Refresh = "refresh";

    public static IReadOnlyList<ShortcutDefinition> All { get; } =
    [
        new(Open, "열기", "선택한 파일·폴더를 엽니다.", Key.Enter, ModifierKeys.None),
        new(Rename, "이름 바꾸기", "선택한 항목의 이름을 바꿉니다.", Key.F2, ModifierKeys.None),
        new(EditWithExternalEditor, "편집(외부 편집기)", "선택한 파일을 외부 편집기로 엽니다.", Key.F4, ModifierKeys.None),
        new(Delete, "삭제", "선택한 항목을 삭제합니다.", Key.Delete, ModifierKeys.None),
        new(NewFolder, "새 폴더", "새 폴더를 만듭니다.", Key.F7, ModifierKeys.None),
        new(NewFile, "새 파일", "새 파일을 만듭니다.", Key.None, ModifierKeys.None),
        new(Search, "파일 찾기", "파일 찾기 창을 엽니다.", Key.F, ModifierKeys.Control),
        new(Properties, "속성", "선택한 항목의 속성을 봅니다.", Key.Enter, ModifierKeys.Alt),
        new(AddFavorite, "즐겨찾기 추가", "선택한 항목을 즐겨찾기에 추가합니다.", Key.D, ModifierKeys.Control),
        new(Copy, "복사", "선택 항목을 클립보드에 복사합니다.", Key.C, ModifierKeys.Control),
        new(Cut, "잘라내기", "선택 항목을 잘라냅니다.", Key.X, ModifierKeys.Control),
        new(Paste, "붙여넣기", "클립보드 내용을 붙여넣습니다.", Key.V, ModifierKeys.Control),
        new(SelectAll, "전체 선택", "활성 패널의 모든 항목을 선택합니다.", Key.A, ModifierKeys.Control),
        new(SelectNone, "선택 해제", "선택을 모두 해제합니다.", Key.Escape, ModifierKeys.None),
        new(GoBack, "뒤로", "이전 폴더로 이동합니다.", Key.Left, ModifierKeys.Alt),
        new(GoForward, "앞으로", "다음 폴더로 이동합니다.", Key.Right, ModifierKeys.Alt),
        new(Refresh, "목록 새로고침", "활성 패널을 새로고침합니다.", Key.F5, ModifierKeys.None)
    ];

    // Practical key list for the shortcut editor: function keys, letters, digits,
    // common named keys, numpad and punctuation. Modifier-only keys are excluded.
    public static IReadOnlyList<Key> SelectableKeys { get; } = BuildSelectableKeys();

    private static IReadOnlyList<Key> BuildSelectableKeys()
    {
        var keys = new List<Key>();

        for (var offset = 0; offset < 24; offset++)
        {
            keys.Add(Key.F1 + offset);
        }

        for (var offset = 0; offset < 26; offset++)
        {
            keys.Add(Key.A + offset);
        }

        for (var offset = 0; offset < 10; offset++)
        {
            keys.Add(Key.D0 + offset);
        }

        keys.AddRange(
        [
            Key.Enter, Key.Escape, Key.Space, Key.Tab, Key.Back, Key.Delete, Key.Insert,
            Key.Home, Key.End, Key.PageUp, Key.PageDown,
            Key.Left, Key.Right, Key.Up, Key.Down
        ]);

        for (var offset = 0; offset < 10; offset++)
        {
            keys.Add(Key.NumPad0 + offset);
        }

        keys.AddRange([Key.Multiply, Key.Add, Key.Subtract, Key.Decimal, Key.Divide]);
        keys.AddRange(
        [
            Key.OemPlus, Key.OemMinus, Key.OemComma, Key.OemPeriod, Key.OemQuestion,
            Key.OemSemicolon, Key.OemQuotes, Key.OemOpenBrackets, Key.OemCloseBrackets,
            Key.OemPipe, Key.OemTilde
        ]);

        return keys;
    }

    public static ShortcutDefinition? Find(string id) =>
        All.FirstOrDefault(definition => string.Equals(definition.Id, id, StringComparison.Ordinal));

    public static ShortcutGesture DefaultGesture(string id) =>
        Find(id)?.DefaultGesture ?? ShortcutGesture.Unassigned;
}
