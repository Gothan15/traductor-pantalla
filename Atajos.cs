using System.Runtime.InteropServices;

namespace TraductorPantalla;

/// <summary>
/// Atajos globales con RegisterHotKey de Win32: funcionan aunque la
/// ventana no tenga el foco. Las constantes y la P/Invoke viven aquí.
/// </summary>
public static class Atajos
{
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_T = 0x54;
    public const uint VK_ESCAPE = 0x1B;

    // Identificadores de cada atajo registrado.
    public const int ID_ALTERNAR = 1;
    public const int ID_ESCAPE = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
