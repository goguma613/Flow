using System;
using System.Collections.Generic;
using System.Text;

namespace Flow.Services;

/// <summary>
/// 전역 단축키 한 조합. Win32 가 아는 숫자와 사람이 읽는 글자 사이를 오간다.
///
/// 창(Avalonia)을 모르는 채로 둔 이유: 시험 프로젝트는 Avalonia 를 참조하지 않는다.
/// 그래서 뷰가 누른 키의 <b>이름</b>("A", "D1", "Space")만 넘겨주고,
/// 이름에서 가상 키 코드를 찾는 일은 여기서 한다. 덕분에 전부 시험할 수 있다.
/// </summary>
public sealed class HotKeyCombo
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>처음부터 쓰던 조합. '기본값으로'가 돌아오는 자리.</summary>
    public static readonly HotKeyCombo Default = new(ModControl | ModAlt, 0x20, "Space");

    private HotKeyCombo(uint modifiers, uint virtualKey, string keyName)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        KeyName = keyName;
    }

    /// <summary>RegisterHotKey 에 넘길 조합키 묶음.</summary>
    public uint Modifiers { get; }

    /// <summary>RegisterHotKey 에 넘길 가상 키 코드.</summary>
    public uint VirtualKey { get; }

    /// <summary>사람이 읽는 키 이름. "Space", "K", "1", "F5" 처럼 짧게.</summary>
    public string KeyName { get; }

    public bool HasControl => (Modifiers & ModControl) != 0;
    public bool HasAlt => (Modifiers & ModAlt) != 0;
    public bool HasShift => (Modifiers & ModShift) != 0;
    public bool HasWin => (Modifiers & ModWin) != 0;

    /// <summary>파일에 적는 꼴. "Ctrl+Alt+Space"</summary>
    public string Saved => Compose("+");

    /// <summary>화면에 보이는 꼴. "Ctrl + Alt + Space"</summary>
    public string Text => Compose(" + ");

    public override string ToString() => Saved;

    /// <summary>
    /// 눌린 키로 조합을 만든다. 만들 수 없으면 <paramref name="reason"/> 에 까닭을 담고 null.
    ///
    /// Ctrl·Alt·Win 중 하나는 반드시 있어야 한다. Shift 만으로 잡으면
    /// 어느 창에서든 그 글자를 못 치게 된다 — 전역 단축키는 남의 타자도 가로채기 때문이다.
    /// </summary>
    public static HotKeyCombo? From(bool ctrl, bool alt, bool shift, bool win, string? keyName, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(keyName) || IsModifierName(keyName))
        {
            reason = "조합키 말고 글자나 기능키도 함께 눌러 주세요.";
            return null;
        }

        if (!TryVirtualKey(keyName, out var vk, out var canonical))
        {
            reason = "이 키로는 잡을 수 없습니다. 글자·숫자·F키를 써 보세요.";
            return null;
        }

        if (!ctrl && !alt && !win)
        {
            reason = "Ctrl · Alt · Win 중 하나는 있어야 합니다.";
            return null;
        }

        var modifiers = 0u;
        if (ctrl) modifiers |= ModControl;
        if (alt) modifiers |= ModAlt;
        if (shift) modifiers |= ModShift;
        if (win) modifiers |= ModWin;

        return new HotKeyCombo(modifiers, vk, canonical);
    }

    /// <summary>저장해 둔 글자를 되읽는다. 비었거나 알아볼 수 없으면 null — 그때는 단축키를 안 쓴다.</summary>
    public static HotKeyCombo? Parse(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return null;

        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;

        foreach (var raw in saved.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = raw.Trim();
            if (piece.Length == 0) continue;

            switch (piece.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows" or "meta" or "cmd": win = true; break;
                default: key = piece; break;
            }
        }

        return From(ctrl, alt, shift, win, key, out _);
    }

    /// <summary>혼자서는 단축키가 될 수 없는 키인지. 잡는 동안 Ctrl 만 눌린 순간을 흘려보내는 데 쓴다.</summary>
    public static bool IsModifierName(string? keyName) => keyName?.ToLowerInvariant() switch
    {
        "leftctrl" or "rightctrl" or "ctrl" or "control" => true,
        "leftalt" or "rightalt" or "alt" => true,
        "leftshift" or "rightshift" or "shift" => true,
        "lwin" or "rwin" or "leftwin" or "rightwin" or "win" or "meta" => true,
        "capital" or "capslock" or "numlock" or "scroll" or "scrolllock" => true,
        "fnlock" or "imemodechange" or "none" => true,
        _ => false
    };

    private string Compose(string glue)
    {
        var text = new StringBuilder();

        if (HasControl) text.Append("Ctrl").Append(glue);
        if (HasAlt) text.Append("Alt").Append(glue);
        if (HasShift) text.Append("Shift").Append(glue);
        if (HasWin) text.Append("Win").Append(glue);

        return text.Append(KeyName).ToString();
    }

    /// <summary>
    /// 키 이름 → 가상 키 코드. Avalonia 의 이름("D1", "Return")과
    /// 우리가 보여주는 이름("1", "Enter")을 둘 다 받아들이고, 보여줄 이름을 돌려준다.
    /// </summary>
    private static bool TryVirtualKey(string keyName, out uint virtualKey, out string canonical)
    {
        var name = keyName.Trim();

        if (Table.TryGetValue(name, out var found))
        {
            virtualKey = found.Key;
            canonical = found.Name;
            return true;
        }

        virtualKey = 0;
        canonical = "";
        return false;
    }

    private static readonly Dictionary<string, (uint Key, string Name)> Table = Build();

    private static Dictionary<string, (uint, string)> Build()
    {
        var table = new Dictionary<string, (uint, string)>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < 26; i++)
        {
            var letter = ((char)('A' + i)).ToString();
            table[letter] = ((uint)(0x41 + i), letter);
        }

        for (var i = 0; i <= 9; i++)
        {
            var digit = i.ToString();
            table[digit] = ((uint)(0x30 + i), digit);
            table["D" + digit] = ((uint)(0x30 + i), digit);       // Avalonia 이름
            table["Numpad" + digit] = ((uint)(0x60 + i), "숫자판 " + digit);
            table["숫자판 " + digit] = ((uint)(0x60 + i), "숫자판 " + digit);   // 적어 둔 글자도 되읽어야 한다
        }

        for (var i = 1; i <= 12; i++)
        {
            var function = "F" + i;
            table[function] = ((uint)(0x6F + i), function);
        }

        Add(table, 0x20, "Space");
        Add(table, 0x0D, "Enter", "Return");
        Add(table, 0x09, "Tab");
        Add(table, 0x08, "Backspace", "Back");
        Add(table, 0x2D, "Insert");
        Add(table, 0x2E, "Delete");
        Add(table, 0x24, "Home");
        Add(table, 0x23, "End");
        Add(table, 0x21, "PageUp", "Prior");
        Add(table, 0x22, "PageDown", "Next");
        Add(table, 0x25, "←", "Left");
        Add(table, 0x26, "↑", "Up");
        Add(table, 0x27, "→", "Right");
        Add(table, 0x28, "↓", "Down");
        Add(table, 0xC0, "`", "OemTilde");
        Add(table, 0xBD, "-", "OemMinus");
        Add(table, 0xBB, "=", "OemPlus");
        Add(table, 0xDB, "[", "OemOpenBrackets");
        Add(table, 0xDD, "]", "OemCloseBrackets");
        Add(table, 0xDC, "\\", "OemBackslash", "OemPipe");
        Add(table, 0xBA, ";", "OemSemicolon");
        Add(table, 0xDE, "'", "OemQuotes");
        Add(table, 0xBC, ",", "OemComma");
        Add(table, 0xBE, ".", "OemPeriod");
        Add(table, 0xBF, "/", "OemQuestion");

        return table;
    }

    private static void Add(Dictionary<string, (uint, string)> table, uint key, string name, params string[] aliases)
    {
        table[name] = (key, name);
        foreach (var alias in aliases) table[alias] = (key, name);
    }
}
