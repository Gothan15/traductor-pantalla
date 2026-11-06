using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TraductorPantalla;

/// <summary>
/// Reconocimiento de texto con el motor OCR integrado de Windows
/// (Windows.Media.Ocr): es local, gratis y no necesita conexión.
/// </summary>
public static class Ocr
{
    /// <summary>
    /// Crea el motor probando primero el idioma preferido (el de origen de
    /// la dirección de traducción: «en-US» para en→es, «es-ES» para es→en),
    /// luego el otro, el del perfil del usuario y, en último caso, el
    /// primero disponible.
    /// Devuelve null si Windows no tiene ningún idioma de OCR instalado.
    /// </summary>
    public static OcrEngine? CrearMotor(string idiomaPreferido = "en-US")
    {
        string otro = idiomaPreferido.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "en-US" : "es-ES";
        var motor = OcrEngine.TryCreateFromLanguage(new Language(idiomaPreferido));
        motor ??= OcrEngine.TryCreateFromLanguage(new Language(otro));
        motor ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (motor is null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
        {
            motor = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
        }
        return motor;
    }

    /// <summary>Reconoce el texto de una captura y devuelve sus líneas con rectángulos.</summary>
    public static async Task<List<LineaOcr>> ReconocerAsync(OcrEngine motor, SoftwareBitmap imagen)
    {
        var resultado = await motor.RecognizeAsync(imagen);
        var lineas = new List<LineaOcr>();

        foreach (var linea in resultado.Lines)
        {
            if (linea.Words.Count == 0) continue;

            // El rectángulo de la línea es la unión de los de sus palabras
            // (vienen en píxeles de la imagen capturada).
            double x = linea.Words.Min(p => p.BoundingRect.X);
            double y = linea.Words.Min(p => p.BoundingRect.Y);
            double derecha = linea.Words.Max(p => p.BoundingRect.X + p.BoundingRect.Width);
            double abajo = linea.Words.Max(p => p.BoundingRect.Y + p.BoundingRect.Height);

            lineas.Add(new LineaOcr
            {
                Texto = linea.Text,
                X = x,
                Y = y,
                Ancho = derecha - x,
                Alto = abajo - y,
            });
        }

        return lineas;
    }
}
