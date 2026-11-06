using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;

namespace TraductorPantalla;

/// <summary>Todo lo capturado de un monitor: imagen, bytes BGRA y geometría.</summary>
public sealed class PantallaCapturada
{
    /// <summary>Rectángulo del monitor en píxeles físicos (puede empezar en negativo).</summary>
    public required RectanguloPx Limites { get; init; }
    /// <summary>Factor de escala del monitor (DPI / 96): 1.0, 1.25, 1.5…</summary>
    public required double Escala { get; init; }
    /// <summary>Imagen capturada, lista para codificarla a PNG.</summary>
    public required BitmapSource Imagen { get; init; }
    /// <summary>Bytes BGRA de la imagen (para muestrear colores de fondo).</summary>
    public required byte[] Bgra { get; init; }
    public required int Stride { get; init; }
    public int AnchoPx => Limites.Ancho;
    public int AltoPx => Limites.Alto;
}

/// <summary>Rectángulo en píxeles físicos.</summary>
public readonly record struct RectanguloPx(int X, int Y, int Ancho, int Alto);

/// <summary>
/// Captura monitores con GDI (BitBlt) sin paquetes externos: el HBITMAP se
/// envuelve como BitmapSource de WPF, de ahí salen los bytes BGRA y el PNG
/// que se decodifica a SoftwareBitmap para el OCR de Windows.
/// </summary>
public static class Captura
{
    private const int SRCCOPY = 0x00CC0020;
    // Sin CAPTUREBLT a propósito: la capa de traducción es una ventana
    // «layered» y así la captura del modo en vivo no se fotografía a sí
    // misma (en la v1.0.0 la capa se quitaba antes de cada captura, pero
    // en vivo eso parpadearía sin parar).

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int nWidth, int nHeight,
        IntPtr hdcSrc, int xSrc, int ySrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType,
        out uint dpiX, out uint dpiY);

    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Lista de monitores con sus límites físicos y su escala DPI.</summary>
    public static List<(RectanguloPx Limites, double Escala)> Monitores()
    {
        var lista = new List<(RectanguloPx, double)>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds; // en píxeles físicos (app PerMonitorV2)
            double escala = 1.0;
            try
            {
                // El HMONITOR se obtiene del punto central del monitor.
                var punto = new PuntoWin32
                {
                    X = (b.Left + b.Right) / 2,
                    Y = (b.Top + b.Bottom) / 2,
                };
                IntPtr hmon = MonitorFromPoint(punto, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (hmon != IntPtr.Zero &&
                    GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    escala = dpiX / 96.0;
                }
            }
            catch
            {
                // Sin shcore (Windows muy viejo): se asume escala 1.
            }
            lista.Add((new RectanguloPx(b.X, b.Y, b.Width, b.Height), escala));
        }
        return lista;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PuntoWin32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PuntoWin32 pt, uint dwFlags);

    /// <summary>Captura un monitor y devuelve imagen + bytes BGRA.</summary>
    public static PantallaCapturada Capturar(RectanguloPx limites, double escala)
    {
        IntPtr hdcPantalla = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcPantalla);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcPantalla, limites.Ancho, limites.Alto);
        IntPtr anterior = SelectObject(hdcMem, hBitmap);
        try
        {
            BitBlt(hdcMem, 0, 0, limites.Ancho, limites.Alto,
                hdcPantalla, limites.X, limites.Y, SRCCOPY);

            BitmapSource origen = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Se normaliza a BGRA32 para poder leer los píxeles tal cual.
            var convertida = new FormatConvertedBitmap(origen, PixelFormats.Bgra32, null, 0);
            int stride = convertida.PixelWidth * 4;
            var bytes = new byte[stride * convertida.PixelHeight];
            convertida.CopyPixels(bytes, stride, 0);
            convertida.Freeze();

            return new PantallaCapturada
            {
                Limites = limites,
                Escala = escala,
                Imagen = convertida,
                Bgra = bytes,
                Stride = stride,
            };
        }
        finally
        {
            SelectObject(hdcMem, anterior);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcPantalla);
        }
    }

    /// <summary>
    /// Convierte la captura a SoftwareBitmap (BGRA8) para el motor OCR.
    /// Con <paramref name="factor"/> &gt; 1 la imagen se amplía antes:
    /// el OCR reconoce así los signos pequeños que a tamaño natural
    /// se pierde (viñetas, puntos, comillas).
    /// </summary>
    public static async Task<SoftwareBitmap> ASoftwareBitmapAsync(BitmapSource imagen, double factor = 1.0)
    {
        BitmapSource fuente = imagen;
        if (factor > 1.01)
        {
            fuente = new TransformedBitmap(imagen, new ScaleTransform(factor, factor));
        }

        // PNG en memoria con el codificador de WPF…
        var codificador = new PngBitmapEncoder();
        codificador.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(fuente));
        using var memoria = new MemoryStream();
        codificador.Save(memoria);
        memoria.Position = 0;

        // …y decodificación con WinRT, que es lo que el OCR consume.
        using var flujo = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var escritor = new Windows.Storage.Streams.DataWriter(flujo))
        {
            escritor.WriteBytes(memoria.ToArray());
            await escritor.StoreAsync();
            escritor.DetachStream();
        }
        flujo.Seek(0);
        var decodificador = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(flujo);
        return await decodificador.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
