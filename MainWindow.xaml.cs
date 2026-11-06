using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Windows.Media.Ocr;

namespace TraductorPantalla;

/// <summary>
/// Ventana de control: elige la dirección, lanza la traducción y quita la capa.
/// El ciclo completo es: ocultar esta ventana → capturar cada monitor →
/// OCR local de Windows → agrupar en bloques → traducir con el modelo
/// Opus-MT local (ONNX, sin red) → pintar la capa.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<OverlayWindow> _capas = new();
    private OcrEngine? _motorOcr;
    private string? _direccionMotorOcr;
    private bool _ocupado;
    private bool _escapeRegistrado;
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var fuente = HwndSource.FromHwnd(_hwnd);
            fuente?.AddHook(HookVentana);
            // Atajo global de alternar: Ctrl+Alt+T.
            Atajos.RegisterHotKey(_hwnd, Atajos.ID_ALTERNAR,
                Atajos.MOD_CONTROL | Atajos.MOD_ALT | Atajos.MOD_NOREPEAT, Atajos.VK_T);
        };
        Closed += (_, _) =>
        {
            Atajos.UnregisterHotKey(_hwnd, Atajos.ID_ALTERNAR);
            DesregistrarEscape();
        };
    }

    // ---------- Atajos globales ----------

    private IntPtr HookVentana(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Atajos.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == Atajos.ID_ALTERNAR)
            {
                Alternar();
                handled = true;
            }
            else if (id == Atajos.ID_ESCAPE)
            {
                QuitarCapas();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void RegistrarEscape()
    {
        // Esc global solo mientras la capa está visible.
        if (_escapeRegistrado) return;
        if (Atajos.RegisterHotKey(_hwnd, Atajos.ID_ESCAPE, Atajos.MOD_NOREPEAT, Atajos.VK_ESCAPE))
        {
            _escapeRegistrado = true;
        }
    }

    private void DesregistrarEscape()
    {
        if (!_escapeRegistrado) return;
        Atajos.UnregisterHotKey(_hwnd, Atajos.ID_ESCAPE);
        _escapeRegistrado = false;
    }

    // ---------- Botones ----------

    private async void BotonTraducir_Click(object sender, RoutedEventArgs e) => await TraducirAsync();

    private void BotonQuitar_Click(object sender, RoutedEventArgs e) => QuitarCapas();

    private void Alternar()
    {
        if (_capas.Count > 0) QuitarCapas();
        else _ = TraducirAsync();
    }

    // ---------- Capa ----------

    private void QuitarCapas(bool actualizarEstado = true)
    {
        foreach (var capa in _capas) capa.Close();
        _capas.Clear();
        DesregistrarEscape();
        if (actualizarEstado) PonerEstado("Listo");
    }

    // ---------- Traducción ----------

    /// <summary>Dirección elegida: «en-es» o «es-en» (también es el nombre de la carpeta del modelo).</summary>
    private string Direccion =>
        (ComboDireccion.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-es";

    private void PonerEstado(string texto) => Estado.Text = texto;

    private async Task TraducirAsync()
    {
        if (_ocupado) return;
        _ocupado = true;
        QuitarCapas(actualizarEstado: false);

        try
        {
            string direccion = Direccion;

            // Sin la carpeta del modelo no hay traducción que valga: se avisa y no se muestra capa.
            if (!TraductorOffline.ModeloDisponible(direccion))
            {
                PonerEstado($"Falta el modelo {direccion}: no está en la carpeta modelos");
                return;
            }

            // El OCR rinde mejor en el idioma de origen de la dirección.
            if (_motorOcr is null || _direccionMotorOcr != direccion)
            {
                _motorOcr = Ocr.CrearMotor(direccion == "es-en" ? "es-ES" : "en-US");
                _direccionMotorOcr = direccion;
            }
            if (_motorOcr is null)
            {
                PonerEstado("Error: Windows no tiene ningún idioma de OCR instalado. " +
                            "Instala uno en Configuración → Hora e idioma → Idioma (opciones de OCR).");
                return;
            }

            // La ventana de control no debe salir en la captura.
            PonerEstado("Capturando…");
            Hide();
            await Task.Delay(200);

            var porPantalla = new List<(PantallaCapturada Pantalla, List<Bloque> Bloques)>();
            int totalBloques = 0;
            foreach (var (limites, escala) in Captura.Monitores())
            {
                var pantalla = Captura.Capturar(limites, escala);

                PonerEstado("Reconociendo texto…");
                using var softwareBitmap = await Captura.ASoftwareBitmapAsync(pantalla.Imagen);
                var lineas = await Ocr.ReconocerAsync(_motorOcr, softwareBitmap);
                var bloques = Agrupador.Agrupar(lineas);

                porPantalla.Add((pantalla, bloques));
                totalBloques += bloques.Count;
            }

            Show();
            Activate();

            if (totalBloques == 0)
            {
                PonerEstado("No se encontró texto en la pantalla.");
                return;
            }

            PonerEstado("Traduciendo…");
            int mostrados = 0;
            foreach (var (pantalla, bloques) in porPantalla)
            {
                if (bloques.Count == 0) continue;
                string[] traducciones = await TraductorOffline.TraducirBloquesAsync(
                    bloques.Select(b => b.Texto).ToArray(), direccion);

                var textos = bloques.Zip(traducciones, (b, t) => (b, t)).ToList();
                var capa = new OverlayWindow(pantalla, textos);
                capa.Mostrar();
                _capas.Add(capa);
                mostrados += textos.Count;
            }

            RegistrarEscape();
            PonerEstado($"Mostrando {mostrados} textos");
        }
        catch (ModeloAusenteException ex)
        {
            QuitarCapas(actualizarEstado: false);
            if (!IsVisible) Show();
            PonerEstado(ex.Message);
        }
        catch (Exception ex)
        {
            // Ningún fallo deja capas fantasma ni la ventana oculta.
            QuitarCapas(actualizarEstado: false);
            if (!IsVisible) Show();
            PonerEstado("Error: " + MensajeCorto(ex));
        }
        finally
        {
            _ocupado = false;
        }
    }

    private static string MensajeCorto(Exception ex)
    {
        string mensaje = ex.Message;
        return mensaje.Length <= 140 ? mensaje : mensaje[..140] + "…";
    }
}
