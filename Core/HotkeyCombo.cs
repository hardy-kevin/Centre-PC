using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace CentrePC.Core;

/// <summary>
/// Combinaison de touches (modificateurs Windows + touche virtuelle) : affichage en français, lecture et écriture
/// dans les formats des autres logiciels (« Alt + Space » de Flow Launcher, objets win/ctrl/alt/shift/code de PowerToys).
/// </summary>
public sealed record HotkeyCombo(int Mods, int Vk)
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public static readonly HotkeyCombo None = new(0, 0);
    public bool IsNone => Vk == 0;
    public bool Has(int m) => (Mods & m) != 0;

    /// <summary>Forme enregistrée dans les réglages : « 3:32 » (modificateurs:touche), « none ».</summary>
    public string Serialize() => IsNone ? "none" : $"{Mods}:{Vk}";

    public static HotkeyCombo Deserialize(string s)
    {
        switch (s)
        {
            case null or "": return new(Ctrl | Alt, 0x20);
            case "none" or "None": return None;
            // Anciens identifiants (première version de l'accès rapide)
            case "CtrlAltSpace": return new(Ctrl | Alt, 0x20);
            case "WinAltSpace": return new(Win | Alt, 0x20);
            case "CtrlShiftSpace": return new(Ctrl | Shift, 0x20);
        }
        var p = s.Split(':');
        return p.Length == 2 && int.TryParse(p[0], out var m) && int.TryParse(p[1], out var k) ? new(m, k) : new(Ctrl | Alt, 0x20);
    }

    // ------------------------------------------------------------------ affichage
    public string Label
    {
        get
        {
            if (IsNone) return "Aucun raccourci";
            var parts = new List<string>();
            if (Has(Win)) parts.Add("Windows");
            if (Has(Ctrl)) parts.Add("Ctrl");
            if (Has(Alt)) parts.Add("Alt");
            if (Has(Shift)) parts.Add("Maj");
            parts.Add(KeyLabel(Vk));
            return string.Join(" + ", parts);
        }
    }

    public override string ToString() => Label;

    public static string KeyLabel(int vk) => vk switch
    {
        0x20 => "Espace", 0x0D => "Entrée", 0x09 => "Tab", 0x1B => "Échap", 0x08 => "Retour arrière", 0x2E => "Suppr", 0x2D => "Inser",
        0x24 => "Origine", 0x23 => "Fin", 0x21 => "Page préc.", 0x22 => "Page suiv.", 0x25 => "Gauche", 0x26 => "Haut", 0x27 => "Droite", 0x28 => "Bas",
        0x13 => "Pause", 0x91 => "Arrêt défil.", 0x2C => "Impr. écran",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Pavé " + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => LayoutName(vk)
    };

    /// <summary>Nom de la touche selon la disposition du clavier (« ² », « ù »…).</summary>
    private static string LayoutName(int vk)
    {
        try
        {
            uint scan = MapVirtualKey((uint)vk, 0);
            var sb = new StringBuilder(32);
            if (scan != 0 && GetKeyNameText((int)(scan << 16), sb, sb.Capacity) > 0) return sb.ToString();
        }
        catch { }
        return $"touche {vk}";
    }

    // ------------------------------------------------------------------ formats des autres logiciels
    /// <summary>« Ctrl + Shift + Space » (noms des touches de WPF, utilisés par Flow Launcher et Wox).</summary>
    public string ToEnglish(string separator = " + ")
    {
        var parts = new List<string>();
        if (Has(Ctrl)) parts.Add("Ctrl");
        if (Has(Alt)) parts.Add("Alt");
        if (Has(Shift)) parts.Add("Shift");
        if (Has(Win)) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey(Vk).ToString());
        return string.Join(separator, parts);
    }

    /// <summary>Lit « Alt + Space », « Ctrl+Shift+K », « Win + Alt + D1 »… ; null si illisible.</summary>
    public static HotkeyCombo ParseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int mods = 0, vk = 0;
        // « Alt + Space », « ctrl+alt+space » (Windows Terminal), « Space, Control » (ShareX)
        foreach (var raw in text.Split('+', ','))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            switch (t.ToLowerInvariant())
            {
                case "ctrl" or "control" or "leftctrl" or "rightctrl": mods |= Ctrl; continue;
                case "alt" or "leftalt" or "rightalt" or "menu": mods |= Alt; continue;
                case "shift" or "leftshift" or "rightshift" or "maj": mods |= Shift; continue;
                case "win" or "windows" or "lwin" or "rwin": mods |= Win; continue;
                case "esc": t = "Escape"; break;
                case "del": t = "Delete"; break;
                case "ins": t = "Insert"; break;
                case "pgup": t = "PageUp"; break;
                case "pgdn": t = "PageDown"; break;
                case "printscreen" or "prtsc": t = "PrintScreen"; break;
            }
            if (t.Length == 1 && char.IsLetterOrDigit(t[0])) vk = char.ToUpperInvariant(t[0]);
            else if (Enum.TryParse<Key>(t, true, out var key)) vk = KeyInterop.VirtualKeyFromKey(key);
            else return null;
        }
        return vk == 0 ? null : new HotkeyCombo(mods, vk);
    }

    /// <summary>Combinaison saisie au clavier dans Centre PC (null tant que seule une touche de modification est enfoncée).</summary>
    public static HotkeyCombo FromKeyboard(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
            return null;
        int mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= Win;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 ? null : new HotkeyCombo(mods, vk);
    }

    /// <summary>Caractère tapé par cette combinaison sur le clavier actuel (AltGr = Ctrl + Alt : « € », « @ »…), ou null.</summary>
    public string TypedCharacter()
    {
        if (Has(Win)) return null;
        try
        {
            var state = new byte[256];
            if (Has(Ctrl)) state[0x11] = state[0xA2] = 0x80;
            if (Has(Alt)) state[0x12] = state[0xA4] = 0x80;
            if (Has(Shift)) state[0x10] = state[0xA0] = 0x80;
            var sb = new StringBuilder(8);
            var layout = GetKeyboardLayout(0);
            int n = ToUnicodeEx((uint)Vk, MapVirtualKey((uint)Vk, 0), state, sb, sb.Capacity, 4, layout);   // 4 : ne pas modifier l'état du clavier
            if (n == 0) return null;
            if (n < 0) ToUnicodeEx((uint)Vk, MapVirtualKey((uint)Vk, 0), state, sb, sb.Capacity, 4, layout);   // touche morte : on la vide
            var s = n > 0 ? sb.ToString(0, n) : null;
            return string.IsNullOrEmpty(s) || char.IsControl(s[0]) || s == " " ? null : s;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetKeyNameText(int lParam, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buffer, int size, uint flags, IntPtr layout);
}
