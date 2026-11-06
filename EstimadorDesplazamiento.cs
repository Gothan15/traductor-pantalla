namespace TraductorPantalla;

/// <summary>
/// Estima cuántos píxeles se ha movido el contenido entre dos capturas
/// del mismo monitor (el caso típico: scroll vertical). Lógica pura
/// sobre los bytes BGRA, sin UI: compara filas muestreadas buscando el
/// desplazamiento vertical que mejor encaja una captura con la otra.
///
/// Devuelve los píxeles que el contenido se ha movido HACIA ABAJO
/// (positivo al bajar, negativo al subir), o 0 si no hay un
/// desplazamiento claro. Con ese número el modo en vivo mueve la capa
/// al instante, sin esperar al siguiente OCR.
/// </summary>
public static class EstimadorDesplazamiento
{
    private const int ColumnasMuestra = 48;
    private const int PasoFilas = 3;
    private const int DesplazamientoMinimo = 6;

    public static int Estimar(
        byte[] previa, byte[] nueva, int ancho, int alto, int stride,
        int maxDy = 700)
    {
        if (previa.Length != nueva.Length || ancho <= 0 || alto <= 60) return 0;
        maxDy = Math.Min(maxDy, alto / 2);

        double costeCero = Coste(previa, nueva, ancho, alto, stride, 0);
        if (costeCero < 2.0) return 0; // prácticamente la misma imagen

        // Búsqueda gruesa cada 3 px (empezando en un múltiplo de 3, para
        // que la rejilla siempre caiga igual) y refinado fino alrededor
        // del mejor: el desplazamiento real queda a ±1 del grueso.
        int mejorDy = 0;
        double mejorCoste = costeCero;
        int inicio = -(maxDy / 3) * 3;
        for (int dy = inicio; dy <= maxDy; dy += 3)
        {
            if (dy == 0) continue;
            double c = Coste(previa, nueva, ancho, alto, stride, dy);
            if (c < mejorCoste) { mejorCoste = c; mejorDy = dy; }
        }
        if (mejorDy != 0)
        {
            for (int dy = mejorDy - 2; dy <= mejorDy + 2; dy++)
            {
                if (dy == 0 || Math.Abs(dy) > maxDy) continue;
                double c = Coste(previa, nueva, ancho, alto, stride, dy);
                if (c < mejorCoste) { mejorCoste = c; mejorDy = dy; }
            }
        }

        // Solo se acepta si el encaje es claramente mejor que no moverse:
        // si no, cualquier cambio de contenido parecería scroll.
        if (Math.Abs(mejorDy) < DesplazamientoMinimo) return 0;
        if (mejorCoste > costeCero * 0.72 || mejorCoste > 22.0) return 0;
        return mejorDy;
    }

    /// <summary>
    /// Coste de suponer que el contenido se movió <paramref name="dy"/>
    /// píxeles hacia abajo: diferencia media de gris entre la fila y de
    /// la captura nueva y la fila y-dy de la previa, muestreadas.
    /// </summary>
    private static double Coste(byte[] previa, byte[] nueva, int ancho, int alto, int stride, int dy)
    {
        long suma = 0;
        long cuenta = 0;
        int pasoX = Math.Max(1, ancho / ColumnasMuestra);

        for (int y = 0; y < alto; y += PasoFilas)
        {
            int yp = y - dy;
            if (yp < 0 || yp >= alto) continue;
            int filaNueva = y * stride;
            int filaPrevia = yp * stride;
            for (int x = pasoX / 2; x < ancho; x += pasoX)
            {
                int iN = filaNueva + x * 4;
                int iP = filaPrevia + x * 4;
                if (iN + 2 >= nueva.Length || iP + 2 >= previa.Length) break;
                int grisN = (nueva[iN] + nueva[iN + 1] + nueva[iN + 2]) / 3;
                int grisP = (previa[iP] + previa[iP + 1] + previa[iP + 2]) / 3;
                suma += Math.Abs(grisN - grisP);
                cuenta++;
            }
        }
        return cuenta == 0 ? double.MaxValue : (double)suma / cuenta;
    }

    /// <summary>
    /// Huella barata de una captura (muestreo de una rejilla de grises)
    /// para decidir si la pantalla cambió lo bastante como para repetir
    /// el OCR. Devuelve valores 0..255; comparar con <see cref="Diferencia"/>.
    /// </summary>
    public static byte[] Huella(byte[] bgra, int ancho, int alto, int stride)
    {
        const int cols = 64, filas = 36;
        var huella = new byte[cols * filas];
        for (int f = 0; f < filas; f++)
        {
            int y = (int)((f + 0.5) * alto / filas);
            for (int c = 0; c < cols; c++)
            {
                int x = (int)((c + 0.5) * ancho / cols);
                int i = y * stride + x * 4;
                huella[f * cols + c] = i + 2 < bgra.Length
                    ? (byte)((bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3)
                    : (byte)0;
            }
        }
        return huella;
    }

    /// <summary>Fracción de muestras de la huella que cambiaron de forma apreciable (0..1).</summary>
    public static double Diferencia(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return 1.0;
        int cambios = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 14) cambios++;
        }
        return (double)cambios / a.Length;
    }
}
