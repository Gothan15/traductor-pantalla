namespace TraductorPantalla;

/// <summary>Una línea reconocida por el OCR, con su rectángulo en píxeles de la captura.</summary>
public sealed class LineaOcr
{
    public required string Texto { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Ancho { get; init; }
    public required double Alto { get; init; }

    public double Derecha => X + Ancho;
    public double Abajo => Y + Alto;
}

/// <summary>Un bloque de texto ya agrupado, listo para traducir y pintar.</summary>
public sealed class Bloque
{
    public required string Texto { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Ancho { get; init; }
    public required double Alto { get; init; }
    public required int NumLineas { get; init; }
}

/// <summary>
/// Agrupa las líneas del OCR en bloques (párrafos). Lógica pura, sin UI:
/// una línea se pega al bloque anterior si está cerca en vertical
/// (hueco menor que 0,7 veces la altura de la línea anterior) y las dos
/// se solapan en horizontal de forma razonable, como en un párrafo.
/// </summary>
public static class Agrupador
{
    public static List<Bloque> Agrupar(IEnumerable<LineaOcr> lineas)
    {
        // Líneas útiles: con texto y al menos una letra (fuera ruido del OCR).
        var utiles = lineas
            .Where(l => !string.IsNullOrWhiteSpace(l.Texto) && l.Texto.Any(char.IsLetter))
            .OrderBy(l => l.Y)
            .ThenBy(l => l.X)
            .ToList();

        var bloques = new List<Bloque>();
        List<LineaOcr>? actual = null;

        void CerrarBloque()
        {
            if (actual is null || actual.Count == 0) return;
            var texto = string.Join(" ", actual.Select(l => l.Texto.Trim()));
            // Se descartan restos sueltos: menos de 2 caracteres o sin letras.
            if (texto.Trim().Length >= 2 && texto.Any(char.IsLetter))
            {
                bloques.Add(new Bloque
                {
                    Texto = texto,
                    X = actual.Min(l => l.X),
                    Y = actual.Min(l => l.Y),
                    Ancho = actual.Max(l => l.Derecha) - actual.Min(l => l.X),
                    Alto = actual.Max(l => l.Abajo) - actual.Min(l => l.Y),
                    NumLineas = actual.Count,
                });
            }
            actual = null;
        }

        foreach (var linea in utiles)
        {
            if (actual is null)
            {
                actual = new List<LineaOcr> { linea };
                continue;
            }

            var anterior = actual[^1];
            double huecoVertical = linea.Y - anterior.Abajo;
            double solape = Math.Min(anterior.Derecha, linea.Derecha) - Math.Max(anterior.X, linea.X);
            bool solapeRazonable = solape > 0.3 * Math.Min(anterior.Ancho, linea.Ancho);

            if (huecoVertical < 0.7 * anterior.Alto && solapeRazonable)
            {
                actual.Add(linea);
            }
            else
            {
                CerrarBloque();
                actual = new List<LineaOcr> { linea };
            }
        }
        CerrarBloque();

        return bloques;
    }
}
