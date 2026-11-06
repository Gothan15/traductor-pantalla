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
///
/// Desde la v1.1 cada texto se pinta ESTIMANDO el original: el tamaño se
/// calcula desde la altura real de las líneas del OCR (en la v1.0.0
/// salía sistemáticamente pequeño), el color se muestrea de los píxeles
/// de las propias letras, y la negrita se adivina por la cobertura de
/// tinta del rectángulo. La fuente exacta de la app de debajo no se
/// puede saber desde los píxeles; se usa «Segoe UI», la de la interfaz
/// de Windows, que es lo más parecido en la práctica.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>
    /// True si Windows aceptó excluir la capa de las capturas (Win10 2004+).
    /// El modo en vivo lo usa para saber si puede capturar sin parpadeos.
    /// </summary>
    public static bool ExcluidaDeCaptura { get; private set; }

    private PantallaCapturada _pantalla;
    private readonly List<Border> _recuadros = new();

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

    /// <summary>
    /// Sustituye todos los textos por los de una captura nueva (modo en
    /// vivo). Se construye fuera del lienzo y se intercambia de una vez,
    /// para que no se vea un parpadeo en blanco entre una y otra.
    /// </summary>
    public void Actualizar(PantallaCapturada pantalla, IReadOnlyList<(Bloque Bloque, string Traduccion)> textos)
    {
        _pantalla = pantalla;
        Lienzo.Children.Clear();
        _recuadros.Clear();
        ConstruirTextos(textos);
    }

    /// <summary>
    /// Mueve toda la capa <paramref name="dyPx"/> píxeles físicos en
    /// vertical (el contenido de debajo hizo scroll). Los recuadros que
    /// salen por completo de la pantalla se ocultan hasta la próxima
    /// actualización por OCR, que los recoloca bien.
    /// </summary>
    public void Desplazar(int dyPx)
    {
        double escala = _pantalla.Escala <= 0 ? 1.0 : _pantalla.Escala;
        double dy = dyPx / escala;
        double altoDip = _pantalla.AltoPx / escala;
        foreach (var recuadro in _recuadros)
        {
            double top = Canvas.GetTop(recuadro) + dy;
            Canvas.SetTop(recuadro, top);
            double altura = recuadro.ActualHeight > 0 ? recuadro.ActualHeight : recuadro.MinHeight;
            recuadro.Visibility = top + altura < 0 || top > altoDip
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void AlCrearHandle(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        // Click-through de verdad y que nunca robe el foco.
        IntPtr estilo = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long nuevo = estilo.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(nuevo));

        // Que la propia capa no salga en las capturas (refuerzo del
        // BitBlt sin CAPTUREBLT que usa Captura).
        try
        {
            if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            {
                ExcluidaDeCaptura = true;
            }
        }
        catch
        {
            // Windows anterior a 2004: sin afinidad, no pasa nada grave.
        }

        // Colocación en píxeles físicos con Win32: WPF y los DPI no se pelean.
        var l = _pantalla.Limites;
        SetWindowPos(hwnd, HWND_TOPMOST, l.X, l.Y, l.Ancho, l.Alto, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void ConstruirTextos(IReadOnlyList<(Bloque Bloque, string Traduccion)> textos)
    {
        foreach (var (bloque, traduccion) in textos)
        {
            var recuadro = CrearRecuadro(bloque, traduccion);
            if (recuadro is null) continue;
            _recuadros.Add(recuadro);
            Lienzo.Children.Add(recuadro);
        }
    }

    private Border? CrearRecuadro(Bloque bloque, string traduccion)
    {
        double escala = _pantalla.Escala <= 0 ? 1.0 : _pantalla.Escala;
        string textoFinal = string.IsNullOrEmpty(bloque.Prefijo)
            || traduccion.TrimStart().StartsWith(bloque.Prefijo.Trim())
            ? traduccion
            : bloque.Prefijo + traduccion;

        // --- Geometría del original, en píxeles de captura ---
        var lineas = bloque.Lineas;
        double altoLineaPx = lineas.Count > 0
            ? Mediana(lineas.Select(l => l.Alto))
            : bloque.Alto / Math.Max(1, bloque.NumLineas);
        double pitchPx = altoLineaPx * 1.3;
        if (lineas.Count >= 2)
        {
            var ordenadas = lineas.OrderBy(l => l.Y).ToList();
            var saltos = new List<double>();
            for (int i = 1; i < ordenadas.Count; i++)
            {
                double salto = ordenadas[i].Y - ordenadas[i - 1].Y;
                if (salto > 2) saltos.Add(salto);
            }
            if (saltos.Count > 0) pitchPx = Mediana(saltos);
            if (pitchPx < altoLineaPx) pitchPx = altoLineaPx * 1.2;
        }

        // --- Tamaño de letra estimado ---
        // La altura que da el OCR para una línea ronda el interlineado
        // «de caja» del texto (≈1,25 veces el cuerpo de la fuente), así
        // que el cuerpo se estima en el 80 % de esa altura, sin pasar
        // nunca del paso de línea (para que los renglones no se monten).
        double fuentePx = Math.Clamp(altoLineaPx * 0.80, 9, 72);
        fuentePx = Math.Min(fuentePx, pitchPx * 0.95);
        double fuente = fuentePx / escala;

        // --- Fondo, color de letra y peso estimados de los píxeles ---
        var (r, g, b) = MuestreadorFondo.Muestrear(
            _pantalla.Bgra, _pantalla.AnchoPx, _pantalla.AltoPx, _pantalla.Stride,
            bloque.X, bloque.Y, bloque.Ancho, bloque.Alto);
        var colorFondo = System.Windows.Media.Color.FromRgb(r, g, b);
        var (colorTexto, cobertura) = MuestreadorFondo.AnalizarTexto(
            _pantalla.Bgra, _pantalla.AnchoPx, _pantalla.AltoPx, _pantalla.Stride,
            bloque.X, bloque.Y, bloque.Ancho, bloque.Alto, (r, g, b));

        System.Windows.Media.Brush pincelTexto;
        if (colorTexto is { } ct
            && Math.Abs(ct.R - r) + Math.Abs(ct.G - g) + Math.Abs(ct.B - b) > 70)
        {
            pincelTexto = new SolidColorBrush(System.Windows.Media.Color.FromRgb(ct.R, ct.G, ct.B));
        }
        else
        {
            pincelTexto = new SolidColorBrush(
                MuestreadorFondo.Luminancia(r, g, b) > 0.55 ? Colors.Black : Colors.White);
        }
        var peso = cobertura >= 0.28 ? FontWeights.SemiBold : FontWeights.Normal;
        var familia = new System.Windows.Media.FontFamily("Segoe UI");

        // --- Caja en unidades WPF ---
        double margen = 2.0 / escala;
        double x = bloque.X / escala - margen;
        double y = bloque.Y / escala - margen;
        double ancho = bloque.Ancho / escala + 2 * margen;
        double altoOriginal = bloque.Alto / escala + 2 * margen;
        bool unaLinea = bloque.NumLineas <= 1;

        if (unaLinea)
        {
            // Ajuste al ancho: si la traducción no cabe, primero se
            // encoge la letra hasta un 72 %; si ni así cabe, la caja se
            // ensancha hacia la derecha en vez de seguir encogiendo,
            // para que se siga leyendo.
            double anchoTexto = MedirAncho(textoFinal, familia, peso, fuente);
            if (anchoTexto > ancho)
            {
                double factor = ancho / anchoTexto;
                if (factor >= 0.72)
                {
                    fuente *= factor;
                }
                else
                {
                    fuente *= 0.72;
                    double necesario = MedirAncho(textoFinal, familia, peso, fuente) + 2 * margen + 2;
                    double sala = _pantalla.AnchoPx / escala - x;
                    ancho = Math.Min(necesario, sala);
                }
            }
        }

        var texto = new TextBlock
        {
            Text = textoFinal,
            FontFamily = familia,
            FontWeight = peso,
            FontSize = fuente,
            Foreground = pincelTexto,
            TextWrapping = unaLinea ? TextWrapping.NoWrap : TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!unaLinea)
        {
            texto.LineHeight = pitchPx / escala;
            texto.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }

        var recuadro = new Border
        {
            Background = new SolidColorBrush(colorFondo),
            Width = ancho,
            Child = texto,
        };
        if (unaLinea)
        {
            recuadro.Height = Math.Max(altoOriginal, fuente * 1.35 + 2 * margen);
        }
        else
        {
            recuadro.MinHeight = altoOriginal;
        }

        Canvas.SetLeft(recuadro, x);
        Canvas.SetTop(recuadro, y);
        return recuadro;
    }

    private static double MedirAncho(string texto, System.Windows.Media.FontFamily familia, FontWeight peso, double fuente)
    {
        var medidor = new TextBlock
        {
            Text = texto,
            FontFamily = familia,
            FontWeight = peso,
            FontSize = fuente,
            TextWrapping = TextWrapping.NoWrap,
        };
        medidor.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return medidor.DesiredSize.Width;
    }

    private static double Mediana(IEnumerable<double> valores)
    {
        var lista = valores.OrderBy(v => v).ToList();
        if (lista.Count == 0) return 0;
        return lista.Count % 2 == 1
            ? lista[lista.Count / 2]
            : (lista[lista.Count / 2 - 1] + lista[lista.Count / 2]) / 2;
    }
}
