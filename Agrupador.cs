using System.Text.RegularExpressions;

namespace TraductorPantalla;

/// <summary>Una palabra reconocida por el OCR, con su rectángulo en píxeles de la captura.</summary>
public sealed class PalabraOcr
{
    public required string Texto { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Ancho { get; init; }
    public required double Alto { get; init; }
}

/// <summary>Una línea reconocida por el OCR, con su rectángulo en píxeles de la captura.</summary>
public sealed class LineaOcr
{
    public required string Texto { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Ancho { get; init; }
    public required double Alto { get; init; }

    /// <summary>Las palabras de la línea (puede venir vacía si el llamante no las aporta).</summary>
    public IReadOnlyList<PalabraOcr> Palabras { get; init; } = Array.Empty<PalabraOcr>();

    public double Derecha => X + Ancho;
    public double Abajo => Y + Alto;
    public double CentroY => Y + Alto / 2;
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

    /// <summary>Las líneas que forman el bloque, en orden de lectura.</summary>
    public IReadOnlyList<LineaOcr> Lineas { get; init; } = Array.Empty<LineaOcr>();

    /// <summary>
    /// Signo de lista detectado al principio (p. ej. «• » o «1. »). Se separa
    /// antes de traducir y se vuelve a pintar tal cual, para que ni el OCR
    /// ni el modelo de traducción se lo traguen.
    /// </summary>
    public string Prefijo { get; init; } = "";
}

/// <summary>
/// Agrupa las líneas del OCR en bloques (párrafos). Lógica pura, sin UI:
/// una línea se pega al bloque anterior si está cerca en vertical
/// (hueco menor que 0,7 veces la altura de la línea anterior) y las dos
/// se solapan en horizontal de forma razonable, como en un párrafo.
///
/// Reglas para no perder signos (corregido tras la v1.0.0, donde las
/// viñetas de las listas desaparecían):
/// - Ya NO se descartan las líneas sin letras: una línea que solo trae
///   un signo puede ser la viñeta de la línea siguiente.
/// - Una viñeta suelta que comparte franja vertical con la línea de su
///   derecha se le pega como prefijo.
/// - Cada renglón de lista (viñeta o número) abre bloque nuevo: una
///   lista no es un párrafo, y cada punto se traduce y pinta por su lado.
/// - El prefijo de lista se separa del texto que va al traductor.
/// </summary>
public static class Agrupador
{
    // Viñeta o numeración al principio de un renglón: «• texto», «- texto»,
    // «1. texto», «2) texto»… El grupo 1 es el signo, el 2 el resto.
    private static readonly Regex RegexPrefijo = new(
        @"^\s*([•·▪▸►◦*-]|\d{1,3}\s*[.)])\s+(.+)$",
        RegexOptions.Compiled);

    private const string SignosSueltos = "•·▪▸►◦*-–—";

    /// <summary>True si el texto empieza por una viñeta o una numeración de lista.</summary>
    public static bool TryExtraerPrefijo(string texto, out string prefijo, out string resto)
    {
        var m = RegexPrefijo.Match(texto);
        if (m.Success)
        {
            prefijo = m.Groups[1].Value.TrimEnd() + " ";
            resto = m.Groups[2].Value;
            return true;
        }
        prefijo = "";
        resto = texto;
        return false;
    }

    /// <summary>True si la línea es solo un signo suelto (una viñeta sin su texto, p. ej.).</summary>
    public static bool EsSignoSuelto(string texto)
    {
        string t = texto.Trim();
        return t.Length is >= 1 and <= 2
               && t.All(c => SignosSueltos.Contains(c));
    }

    /// <summary>True si la línea contiene alguna letra o algún dígito.</summary>
    private static bool TieneContenido(string texto) =>
        texto.Any(c => char.IsLetterOrDigit(c));

    public static List<Bloque> Agrupar(IEnumerable<LineaOcr> lineasEntrada)
    {
        var ordenadas = lineasEntrada
            .Where(l => !string.IsNullOrWhiteSpace(l.Texto))
            .OrderBy(l => l.Y)
            .ThenBy(l => l.X)
            .ToList();

        // Paso 1: pegar cada signo suelto a la línea de texto que tenga a su
        // derecha en la misma franja vertical (su renglón de lista). Si no
        // tiene ninguna, el signo se descarta: pintarlo solo sería ruido.
        // Se resuelven primero TODOS los signos (su renglón puede ir antes
        // en el orden por Y) y luego se añaden las líneas que quedan.
        var utiles = new List<LineaOcr>();
        var consumidas = new HashSet<LineaOcr>();
        foreach (var signo in ordenadas.Where(l => EsSignoSuelto(l.Texto) && !TieneContenido(l.Texto)))
        {
            var destino = ordenadas.FirstOrDefault(o =>
                !ReferenceEquals(o, signo)
                && !consumidas.Contains(o)
                && TieneContenido(o.Texto)
                && o.X >= signo.X
                && Math.Abs(o.CentroY - signo.CentroY) <= Math.Max(signo.Alto, o.Alto) * 0.75);
            if (destino is null) continue;
            consumidas.Add(signo);
            consumidas.Add(destino);
            utiles.Add(new LineaOcr
            {
                Texto = signo.Texto.Trim() + " " + destino.Texto.Trim(),
                X = signo.X,
                Y = Math.Min(signo.Y, destino.Y),
                Ancho = destino.Derecha - signo.X,
                Alto = Math.Max(signo.Abajo, destino.Abajo) - Math.Min(signo.Y, destino.Y),
                Palabras = destino.Palabras,
            });
        }
        foreach (var linea in ordenadas)
        {
            if (consumidas.Contains(linea)) continue;
            if (EsSignoSuelto(linea.Texto)) continue; // suelto y sin renglón: ruido
            if (!TieneContenido(linea.Texto))
            {
                // Ruido del OCR: un signo raro sin contenido ni forma de viñeta.
                continue;
            }
            utiles.Add(linea);
        }
        utiles = utiles.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();

        // Paso 2: agrupar en bloques como antes, pero un renglón de lista
        // siempre abre bloque nuevo (y el siguiente también).
        var bloques = new List<Bloque>();
        List<(LineaOcr Linea, string Prefijo, string Texto)>? actual = null;
        bool actualEsLista = false;

        void CerrarBloque()
        {
            if (actual is null || actual.Count == 0) return;
            var texto = string.Join(" ", actual.Select(l => l.Texto.Trim()));
            if (texto.Trim().Length >= 2 && TieneContenido(texto))
            {
                var ls = actual.Select(a => a.Linea).ToList();
                bloques.Add(new Bloque
                {
                    Texto = texto,
                    X = ls.Min(l => l.X),
                    Y = ls.Min(l => l.Y),
                    Ancho = ls.Max(l => l.Derecha) - ls.Min(l => l.X),
                    Alto = ls.Max(l => l.Abajo) - ls.Min(l => l.Y),
                    NumLineas = ls.Count,
                    Lineas = ls,
                    Prefijo = actual[0].Prefijo,
                });
            }
            actual = null;
            actualEsLista = false;
        }

        foreach (var linea in utiles)
        {
            bool esLista = TryExtraerPrefijo(linea.Texto.Trim(), out string prefijo, out string resto);
            var entrada = (linea, esLista ? prefijo : "", esLista ? resto : linea.Texto.Trim());

            if (actual is null)
            {
                actual = new() { entrada };
                actualEsLista = esLista;
                continue;
            }

            if (esLista || actualEsLista)
            {
                CerrarBloque();
                actual = new() { entrada };
                actualEsLista = esLista;
                continue;
            }

            var anterior = actual[^1].Linea;
            double huecoVertical = linea.Y - anterior.Abajo;
            double solape = Math.Min(anterior.Derecha, linea.Derecha) - Math.Max(anterior.X, linea.X);
            bool solapeRazonable = solape > 0.3 * Math.Min(anterior.Ancho, linea.Ancho);

            if (huecoVertical < 0.7 * anterior.Alto && solapeRazonable)
            {
                actual.Add(entrada);
            }
            else
            {
                CerrarBloque();
                actual = new() { entrada };
                actualEsLista = esLista;
            }
        }
        CerrarBloque();

        return bloques;
    }
}
