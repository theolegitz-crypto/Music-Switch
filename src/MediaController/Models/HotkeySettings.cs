using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace MediaController.Models;

public sealed class HotkeySettings
{
    public bool Ctrl { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    public bool Win { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Key Key { get; set; } = Key.None;

    /// <summary>A modifier is required, otherwise the app would swallow a plain key globally.</summary>
    [JsonIgnore]
    public bool IsValid => Key != Key.None && (Ctrl || Alt || Shift || Win);

    public static HotkeySettings Create(bool ctrl, bool alt, bool shift, bool win, Key key) =>
        new() { Ctrl = ctrl, Alt = alt, Shift = shift, Win = win, Key = key };

    public HotkeySettings Clone() =>
        new() { Ctrl = Ctrl, Alt = Alt, Shift = Shift, Win = Win, Key = Key };

    public bool SameAs(HotkeySettings other) =>
        Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift && Win == other.Win && Key == other.Key;

    public override string ToString()
    {
        if (Key == Key.None)
        {
            return "Not assigned";
        }

        var text = new StringBuilder();
        if (Ctrl) text.Append("Ctrl + ");
        if (Alt) text.Append("Alt + ");
        if (Shift) text.Append("Shift + ");
        if (Win) text.Append("Win + ");
        text.Append(Describe(Key));
        return text.ToString();
    }

    /// <summary>
    /// Turns a WPF Key into something a person recognises. Users should never see
    /// "Prior", "D1" or "Oem3" on screen.
    /// </summary>
    public static string Describe(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return "Num " + (int)(key - Key.NumPad0);
        }

        return key switch
        {
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            Key.Capital => "CapsLock",
            Key.Snapshot => "PrintScreen",
            Key.Scroll => "ScrollLock",
            Key.Pause => "Pause",
            Key.Add => "Num +",
            Key.Subtract => "Num -",
            Key.Multiply => "Num *",
            Key.Divide => "Num /",
            Key.Decimal => "Num .",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemTilde => "`",
            Key.OemQuestion => "/",
            Key.OemPipe => "\\",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemBackslash => "\\",
            Key.None => "Not assigned",
            _ => key.ToString()
        };
    }
}
