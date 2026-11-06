using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace TraductorPantalla;

/// <summary>
/// La capa transparente que pinta las traducciones encima de la pantalla,
/// cada una en la posición del texto original. Una ventana por monitor.
/// Es «click-through»: los clics la atraviesan y llegan a la app de debajo.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private readonly PantallaCapturada _pantalla;

    public OverlayWindow(PantallaCapturada pantalla, IReadOnlyList<(Bloque Bloque, string Traduccion)> textos)
    {
        InitializeComponent();
        _pantalla = pantalla;
        SourceInitialized += AlCrearHandle;
        ConstruirTextos(textos);
    }

    /// <summary>Crea el handle sin mostrar aún, aplica estilos y posición, y muestra la capa.</summary>
    public void Mostrar()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Show();
    }

    private void AlCrearHandle(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        // Click-through de verdad y que nunca robe el foco.
        IntPtr estilo = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long nuevo = estilo.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(nuevo));

        // Colocación en píxeles físicos con Win32: WPF y los DPI no se pelean.
        var l = _pantalla.Limites;
        SetWindowPos(hwnd, HWND_TOPMOST, l.X, l.Y, l.Ancho, l.Alto, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void ConstruirTextos(IReadOnlyList<(Bloque Bloque, string Traduccion)> textos)
    {
        double escala = _pantalla.Escala <= 0 ? 1.0 : _pantalla.Escala;

        foreach (var (bloque, traduccion) in textos)
        {
            // Fondo: color muestreado alrededor del texto original.
            var (r, g, b) = MuestreadorFondo.Muestrear(
                _pantalla.Bgra, _pantalla.AnchoPx, _pantalla.AltoPx, _pantalla.Stride,
                bloque.X, bloque.Y, bloque.Ancho, bloque.Alto);
            var colorFondo = System.Windows.Media.Color.FromRgb(r, g, b);
            bool fondoClaro = MuestreadorFondo.Luminancia(r, g, b) > 0.55;

            // El recuadro tapa el texto original: 2 px más por cada lado,
            // convertido de píxeles a unidades WPF dividiendo por la escala.
            double margen = 2.0 / escala;
            double x = bloque.X / escala - margen;
            double y = bloque.Y / escala - margen;
            double ancho = bloque.Ancho / escala + 2 * margen;
            double alto = bloque.Alto / escala + 2 * margen;

            double fuente = Math.Clamp(bloque.Alto / escala * 0.62 / Math.Max(1, bloque.NumLineas), 9, 40);

            var texto = new TextBlock
            {
                Text = traduccion,
                TextWrapping = TextWrapping.Wrap,
                FontSize = fuente,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(fondoClaro ? Colors.Black : Colors.White),
            };

            var recuadro = new Border
            {
                Background = new SolidColorBrush(colorFondo),
                Width = ancho,
                Height = alto,
                Child = texto,
            };

            Canvas.SetLeft(recuadro, x);
            Canvas.SetTop(recuadro, y);
            Lienzo.Children.Add(recuadro);
        }
    }
}
