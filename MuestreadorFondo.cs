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

    /// <summary>
    /// Analiza los píxeles de DENTRO del rectángulo de un texto para
    /// estimar dos cosas del original: su color (el promedio de los
    /// píxeles que más se alejan del fondo: las letras) y su cobertura
    /// (qué fracción del rectángulo es letra: un texto en negrita cubre
    /// claramente más que uno normal). Con eso la traducción se pinta
    /// del mismo color y con un peso parecido, no siempre en negro o
    /// blanco puros como en la v1.0.0.
    /// Si hay muy pocos píxeles de letra, devuelve null en el color.
    /// </summary>
    public static ((byte R, byte G, byte B)? Color, double Cobertura) AnalizarTexto(
        byte[] bgra, int anchoImg, int altoImg, int stride,
        double x, double y, double ancho, double alto,
        (byte R, byte G, byte B) fondo)
    {
        int x0 = Math.Max(0, (int)Math.Floor(x));
        int y0 = Math.Max(0, (int)Math.Floor(y));
        int x1 = Math.Min(anchoImg - 1, (int)Math.Ceiling(x + ancho));
        int y1 = Math.Min(altoImg - 1, (int)Math.Ceiling(y + alto));
        if (x1 < x0 || y1 < y0) return (null, 0);

        // Muestreo adaptativo: como mucho ~6000 píxeles por rectángulo.
        double area = (double)(x1 - x0 + 1) * (y1 - y0 + 1);
        int paso = Math.Max(1, (int)Math.Sqrt(area / 6000.0));

        long sumaR = 0, sumaG = 0, sumaB = 0, letras = 0, total = 0;
        for (int py = y0; py <= y1; py += paso)
        {
            for (int px = x0; px <= x1; px += paso)
            {
                int i = py * stride + px * 4;
                if (i + 2 >= bgra.Length) continue;
                total++;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int distancia = Math.Abs(r - fondo.R) + Math.Abs(g - fondo.G) + Math.Abs(b - fondo.B);
                // Letra = píxel que se aleja del fondo de forma clara
                // (el antialiasing de los bordes queda por debajo).
                if (distancia > 110)
                {
                    letras++;
                    sumaR += r; sumaG += g; sumaB += b;
                }
            }
        }

        if (total == 0) return (null, 0);
        double cobertura = (double)letras / total;
        if (letras < 12) return (null, cobertura);
        return (((byte)(sumaR / letras), (byte)(sumaG / letras), (byte)(sumaB / letras)), cobertura);
    }
}
