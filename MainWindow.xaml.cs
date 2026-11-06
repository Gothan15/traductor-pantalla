using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Windows.Media.Ocr;

namespace TraductorPantalla;

/// <summary>
/// Ventana de control: elige la dirección, lanza la traducción y quita la capa.
/// El ciclo es: capturar cada monitor → OCR local de Windows → agrupar en
/// bloques → traducir con el modelo Opus-MT local (ONNX, sin red) → pintar
/// la capa.
///
/// Dos modos:
/// - Foto (el de la v1.0.0): una captura, una capa quieta.
/// - En vivo (v1.1): un bucle que vigila la pantalla. Si el contenido se
///   desplaza (scroll), mueve la capa con él al instante estimando el
///   desplazamiento entre capturas; si la pantalla cambia, repite el OCR
///   y retraduce solo lo nuevo (lo ya traducido sale de la caché del
///   motor, sin recalcular). La ventana de control se oculta mientras
///   dura; se sale con Ctrl+Alt+T o Esc.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<int, OverlayWindow> _capaPorMonitor = new();
    private OcrEngine? _motorOcr;
    private string? _direccionMotorOcr;
    private bool _ocupado;
    private bool _escapeRegistrado;
    private IntPtr _hwnd;

    // Estado del modo en vivo.
    private CancellationTokenSource? _vivoCts;
    private bool _enVivo;
    private readonly Dictionary<int, PantallaCapturada> _capturaAnterior = new();
    private readonly Dictionary<int, byte[]> _huellaDeLoPintado = new();
    private readonly Dictionary<int, long> _ultimoOcrMs = new();

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
            _vivoCts?.Cancel();
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
        if (_enVivo || _capaPorMonitor.Count > 0) QuitarCapas();
        else _ = TraducirAsync();
    }

    // ---------- Capa ----------

    private void QuitarCapas(bool actualizarEstado = true)
    {
        bool eraVivo = _enVivo;
        _enVivo = false;
        _vivoCts?.Cancel();
        _vivoCts = null;

        foreach (var capa in _capaPorMonitor.Values) capa.Close();
        _capaPorMonitor.Clear();
        _capturaAnterior.Clear();
        _huellaDeLoPintado.Clear();
        _ultimoOcrMs.Clear();
        DesregistrarEscape();

        if (eraVivo && !IsVisible) Show();
        if (actualizarEstado) PonerEstado(eraVivo ? "Modo en vivo detenido" : "Listo");
    }

    // ---------- Preparación compartida ----------

    /// <summary>Dirección elegida: «en-es» o «es-en» (también es el nombre de la carpeta del modelo).</summary>
    private string Direccion =>
        (ComboDireccion.SelectedItem as ComboBoxItem)?.Tag as string ?? "en-es";

    private void PonerEstado(string texto) => Estado.Text = texto;

    /// <summary>
    /// Comprueba el modelo y deja listo el motor OCR en el idioma de
    /// origen. Devuelve null si todo va bien, o el mensaje de error.
    /// </summary>
    private string? Preparar(string direccion)
    {
        if (!TraductorOffline.ModeloDisponible(direccion))
        {
            return $"Falta el modelo {direccion}: no está en la carpeta modelos";
        }
        if (_motorOcr is null || _direccionMotorOcr != direccion)
        {
            _motorOcr = Ocr.CrearMotor(direccion == "es-en" ? "es-ES" : "en-US");
            _direccionMotorOcr = direccion;
        }
        if (_motorOcr is null)
        {
            return "Error: Windows no tiene ningún idioma de OCR instalado. " +
                   "Instala uno en Configuración → Hora e idioma → Idioma (opciones de OCR).";
        }
        return null;
    }

    /// <summary>
    /// OCR + agrupación + traducción de UNA captura ya hecha, y pinta o
    /// actualiza la capa de ese monitor. Devuelve cuántos textos pinta.
    /// </summary>
    private async Task<int> ProcesarCapturaAsync(
        int indiceMonitor, PantallaCapturada pantalla, string direccion,
        CancellationToken token)
    {
        double factor = Ocr.FactorAmpliacion(pantalla.AnchoPx, pantalla.AltoPx);
        using var softwareBitmap = await Captura.ASoftwareBitmapAsync(pantalla.Imagen, factor);
        var lineas = await Ocr.ReconocerAsync(_motorOcr!, softwareBitmap, factor);
        var bloques = Agrupador.Agrupar(lineas);

        if (bloques.Count == 0)
        {
            if (_capaPorMonitor.TryGetValue(indiceMonitor, out var capaVacia))
            {
                capaVacia.Actualizar(pantalla,
                    Array.Empty<(Bloque Bloque, string Traduccion)>());
            }
            return 0;
        }

        string[] traducciones = await TraductorOffline.TraducirBloquesAsync(
            bloques.Select(b => b.Texto).ToArray(), direccion);
        if (token.IsCancellationRequested) return 0;

        var textos = bloques.Zip(traducciones, (b, t) => (b, t)).ToList();
        if (_capaPorMonitor.TryGetValue(indiceMonitor, out var capa))
        {
            capa.Actualizar(pantalla, textos);
        }
        else
        {
            capa = new OverlayWindow(pantalla, textos);
            capa.Mostrar();
            _capaPorMonitor[indiceMonitor] = capa;
        }
        return textos.Count;
    }

    // ---------- Modo foto (una captura) ----------

    private async Task TraducirAsync()
    {
        if (_ocupado || _enVivo) return;

        if (CheckVivo.IsChecked == true)
        {
            await IniciarVivoAsync();
            return;
        }

        _ocupado = true;
        QuitarCapas(actualizarEstado: false);

        try
        {
            string direccion = Direccion;
            if (Preparar(direccion) is { } error)
            {
                PonerEstado(error);
                return;
            }

            // La ventana de control no debe salir en la captura.
            PonerEstado("Capturando…");
            Hide();
            await Task.Delay(200);

            int mostrados = 0;
            var monitores = Captura.Monitores();
            for (int i = 0; i < monitores.Count; i++)
            {
                var pantalla = Captura.Capturar(monitores[i].Limites, monitores[i].Escala);
                PonerEstado("Reconociendo y traduciendo…");
                mostrados += await ProcesarCapturaAsync(i, pantalla, direccion, CancellationToken.None);
            }

            Show();
            Activate();

            if (mostrados == 0)
            {
                PonerEstado("No se encontró texto en la pantalla.");
                return;
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

    // ---------- Modo en vivo ----------

    private async Task IniciarVivoAsync()
    {
        string direccion = Direccion;
        if (Preparar(direccion) is { } error)
        {
            PonerEstado(error);
            return;
        }

        _enVivo = true;
        _vivoCts = new CancellationTokenSource();
        var token = _vivoCts.Token;
        RegistrarEscape();

        // La ventana de control se oculta mientras dura el modo en vivo:
        // si no, saldría en las capturas y se traduciría a sí misma.
        Hide();

        bool primera = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var monitores = Captura.Monitores();
                for (int i = 0; i < monitores.Count && !token.IsCancellationRequested; i++)
                {
                    // Si Windows no excluyó la capa de las capturas, se
                    // oculta solo durante el BitBlt (milisegundos) para
                    // que la captura no la incluya.
                    bool tapar = !OverlayWindow.ExcluidaDeCaptura && _capaPorMonitor.Count > 0;
                    if (tapar) foreach (var c in _capaPorMonitor.Values) c.Hide();
                    var pantalla = Captura.Capturar(monitores[i].Limites, monitores[i].Escala);
                    if (tapar) foreach (var c in _capaPorMonitor.Values) c.Show();

                    // ¿Scroll? La capa se mueve con el contenido al
                    // instante, sin esperar al OCR.
                    if (_capturaAnterior.TryGetValue(i, out var previa)
                        && previa.AnchoPx == pantalla.AnchoPx && previa.AltoPx == pantalla.AltoPx
                        && _capaPorMonitor.TryGetValue(i, out var capaScroll))
                    {
                        int dy = EstimadorDesplazamiento.Estimar(
                            previa.Bgra, pantalla.Bgra,
                            pantalla.AnchoPx, pantalla.AltoPx, pantalla.Stride);
                        if (dy != 0) capaScroll.Desplazar(dy);
                    }
                    _capturaAnterior[i] = pantalla;

                    // ¿Cambió la pantalla respecto a lo último pintado?
                    var huella = EstimadorDesplazamiento.Huella(
                        pantalla.Bgra, pantalla.AnchoPx, pantalla.AltoPx, pantalla.Stride);
                    bool cambio = !_huellaDeLoPintado.TryGetValue(i, out var huellaPintada)
                                  || EstimadorDesplazamiento.Diferencia(huella, huellaPintada) > 0.015;
                    bool esperaCumplida = !_ultimoOcrMs.TryGetValue(i, out var ultimo)
                                          || Environment.TickCount64 - ultimo >= 450;

                    if (cambio && (primera || esperaCumplida))
                    {
                        await ProcesarCapturaAsync(i, pantalla, direccion, token);
                        _huellaDeLoPintado[i] = huella;
                        _ultimoOcrMs[i] = Environment.TickCount64;
                    }
                }
                primera = false;
                await Task.Delay(130, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Salida normal del modo en vivo.
        }
        catch (Exception ex)
        {
            QuitarCapas(actualizarEstado: false);
            if (!IsVisible) Show();
            PonerEstado("Error en el modo en vivo: " + MensajeCorto(ex));
            return;
        }

        // Si se salió con Ctrl+Alt+T o Esc, QuitarCapas ya volvió a
        // mostrar la ventana; si se cerró la app, esto no debe reventar.
        try
        {
            if (!IsVisible && _enVivo) Show();
        }
        catch (InvalidOperationException)
        {
            // La ventana ya estaba cerrada.
        }
    }

    private static string MensajeCorto(Exception ex)
    {
        string mensaje = ex.Message;
        return mensaje.Length <= 140 ? mensaje : mensaje[..140] + "…";
    }
}
