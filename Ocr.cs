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

    /// <summary>
    /// Factor de ampliación recomendado para pasar la captura por el OCR.
    /// El motor de Windows reconoce mucho mejor los signos pequeños (la
    /// viñeta de una lista, un punto, unas comillas) cuando el texto le
    /// llega más grande; con la captura a tamaño natural se los traga.
    /// Se amplía solo si la imagen es pequeña, sin pasar del máximo que
    /// el motor acepta.
    /// </summary>
    public static double FactorAmpliacion(int anchoPx, int altoPx)
    {
        int mayor = Math.Max(anchoPx, altoPx);
        if (mayor <= 0) return 1.0;
        double factor = mayor switch
        {
            < 1200 => 1.75,
            < 1800 => 1.4,
            _ => 1.0,
        };
        // Nunca por encima del máximo del motor (con margen).
        double tope = 2500.0 / mayor;
        return Math.Max(1.0, Math.Min(factor, tope));
    }

    /// <summary>
    /// Reconoce el texto de una captura y devuelve sus líneas con
    /// rectángulos. <paramref name="escala"/> es el factor al que la
    /// imagen se amplió antes del OCR (1 = tamaño natural): los
    /// rectángulos vuelven divididos a píxeles de la captura original.
    /// Cada línea trae también sus palabras, que sirven para estimar
    /// el tamaño real de la letra al pintar la traducción.
    /// </summary>
    public static async Task<List<LineaOcr>> ReconocerAsync(
        OcrEngine motor, SoftwareBitmap imagen, double escala = 1.0)
    {
        if (escala <= 0) escala = 1.0;
        var resultado = await motor.RecognizeAsync(imagen);
        var lineas = new List<LineaOcr>();

        foreach (var linea in resultado.Lines)
        {
            if (linea.Words.Count == 0) continue;

            var palabras = linea.Words.Select(p => new PalabraOcr
            {
                Texto = p.Text,
                X = p.BoundingRect.X / escala,
                Y = p.BoundingRect.Y / escala,
                Ancho = p.BoundingRect.Width / escala,
                Alto = p.BoundingRect.Height / escala,
            }).ToList();

            // El rectángulo de la línea es la unión de los de sus palabras.
            double x = palabras.Min(p => p.X);
            double y = palabras.Min(p => p.Y);
            double derecha = palabras.Max(p => p.X + p.Ancho);
            double abajo = palabras.Max(p => p.Y + p.Alto);

            lineas.Add(new LineaOcr
            {
                Texto = linea.Text,
                X = x,
                Y = y,
                Ancho = derecha - x,
                Alto = abajo - y,
                Palabras = palabras,
            });
        }

        return lineas;
    }
}
