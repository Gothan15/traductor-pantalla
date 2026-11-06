namespace TraductorPantalla;

/// <summary>
/// Muestra el color de fondo que rodea un rectángulo de texto en la captura.
/// La captura viaja como bytes BGRA (4 bytes por píxel); se promedian los
/// píxeles del anillo justo por fuera del rectángulo y, si el anillo se sale
/// de la imagen, los de los bordes internos. Función pura, sin UI.
/// </summary>
public static class MuestreadorFondo
{
    /// <summary>Devuelve el color promedio (R, G, B) alrededor del rectángulo dado.</summary>
    public static (byte R, byte G, byte B) Muestrear(
        byte[] bgra, int anchoImg, int altoImg, int stride,
        double x, double y, double ancho, double alto)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = (int)Math.Ceiling(x + ancho);
        int y1 = (int)Math.Ceiling(y + alto);

        long sumaR = 0, sumaG = 0, sumaB = 0, cuenta = 0;

        void Sumar(int px, int py)
        {
            if (px < 0 || py < 0 || px >= anchoImg || py >= altoImg) return;
            int i = py * stride + px * 4;
            if (i + 2 >= bgra.Length) return;
            sumaB += bgra[i];
            sumaG += bgra[i + 1];
            sumaR += bgra[i + 2];
            cuenta++;
        }

        // Anillo exterior: 2 píxeles por fuera del rectángulo.
        for (int grosor = 1; grosor <= 2; grosor++)
        {
            int ax0 = x0 - grosor, ay0 = y0 - grosor;
            int ax1 = x1 + grosor, ay1 = y1 + grosor;
            for (int px = ax0; px <= ax1; px++) { Sumar(px, ay0); Sumar(px, ay1); }
            for (int py = ay0; py <= ay1; py++) { Sumar(ax0, py); Sumar(ax1, py); }
        }

        // Si el anillo quedó (casi) todo fuera de la imagen, se muestrea el
        // borde interno del propio rectángulo.
        if (cuenta < 8)
        {
            for (int px = x0; px <= x1; px++) { Sumar(px, y0); Sumar(px, y1); }
            for (int py = y0; py <= y1; py++) { Sumar(x0, py); Sumar(x1, py); }
        }

        if (cuenta == 0) return (255, 255, 255);
        return ((byte)(sumaR / cuenta), (byte)(sumaG / cuenta), (byte)(sumaB / cuenta));
    }

    /// <summary>Luminancia percibida (0..1) de un color, para decidir texto negro o blanco.</summary>
    public static double Luminancia(byte r, byte g, byte b)
        => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
}
