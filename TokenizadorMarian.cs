using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TraductorPantalla;

/// <summary>
/// Lector mínimo del formato protobuf de un modelo SentencePiece (.model).
/// Solo extrae la lista de piezas con su puntuación y tipo.
/// Formato (sentencepiece_model.proto): ModelProto.pieces (campo 1) es un
/// mensaje repetido SentencePiece { piece=1 (string), score=2 (float), type=3 }.
/// </summary>
internal static class LectorSpm
{
    public sealed record Pieza(string Texto, float Puntuacion, int Tipo);

    public static List<Pieza> LeerPiezas(string rutaModelo)
    {
        byte[] datos = File.ReadAllBytes(rutaModelo);
        var piezas = new List<Pieza>();
        foreach (var (campo, tipoCable, carga) in LeerCampos(datos))
        {
            if (campo != 1 || tipoCable != 2) continue; // pieces
            string? texto = null;
            float puntuacion = 0f;
            int tipo = 1;
            foreach (var (c2, t2, carga2) in LeerCampos(carga))
            {
                if (c2 == 1 && t2 == 2) texto = Encoding.UTF8.GetString(carga2);
                else if (c2 == 2 && t2 == 5) puntuacion = BitConverter.ToSingle(carga2);
                else if (c2 == 3 && t2 == 0) tipo = (int)LeerVarintEn(carga2);
            }
            if (texto is not null) piezas.Add(new Pieza(texto, puntuacion, tipo));
        }
        return piezas;
    }

    private static ulong LeerVarintEn(byte[] datos)
    {
        ulong r = 0; int desp = 0; int i = 0;
        while (i < datos.Length)
        {
            byte b = datos[i++];
            r |= (ulong)(b & 0x7F) << desp;
            if ((b & 0x80) == 0) break;
            desp += 7;
        }
        return r;
    }

    /// <summary>Itera los campos de un mensaje protobuf (varint, longitud-prefijada, 32 y 64 bits).</summary>
    public static IEnumerable<(int Campo, int TipoCable, byte[] Carga)> LeerCampos(byte[] datos)
    {
        int i = 0;
        while (i < datos.Length)
        {
            ulong tag = LeerVarint(datos, ref i);
            int campo = (int)(tag >> 3);
            int tipoCable = (int)(tag & 7);
            switch (tipoCable)
            {
                case 0:
                {
                    int ini = i;
                    LeerVarint(datos, ref i);
                    yield return (campo, tipoCable, datos[ini..i]);
                    break;
                }
                case 2:
                {
                    int len = (int)LeerVarint(datos, ref i);
                    yield return (campo, tipoCable, datos[i..(i + len)]);
                    i += len;
                    break;
                }
                case 5:
                    yield return (campo, tipoCable, datos[i..(i + 4)]);
                    i += 4;
                    break;
                case 1:
                    yield return (campo, tipoCable, datos[i..(i + 8)]);
                    i += 8;
                    break;
                default:
                    yield break;
            }
        }
    }

    private static ulong LeerVarint(byte[] datos, ref int i)
    {
        ulong r = 0; int desp = 0;
        while (true)
        {
            byte b = datos[i++];
            r |= (ulong)(b & 0x7F) << desp;
            if ((b & 0x80) == 0) break;
            desp += 7;
        }
        return r;
    }
}

/// <summary>
/// Tokenizador de los modelos Opus-MT (Marian): el .spm de origen trocea el
/// texto y cada PIEZA (cadena) se busca en el vocabulario conjunto
/// (vocab.json) para obtener el id que el modelo espera; al final se añade
/// &lt;/s&gt; (id 0). La descodificación es el camino inverso por vocab.json.
///
/// Los .spm de Opus-MT son modelos UNIGRAM: la codificación es el algoritmo
/// de Viterbi que maximiza la suma de puntuaciones de las piezas. Está
/// comprobado en la prueba de humo (prueba-onnx): con BPE la segmentación
/// salía mal («▁w|or|ld») y la traducción se resentía; con unigram sale
/// «▁world» y la traducción es correcta.
/// </summary>
public sealed class TokenizadorMarian
{
    public const int IdEos = 0;      // </s>
    public const int IdUnk = 1;      // <unk>
    public const int IdPad = 65000;  // <pad> — también token inicial del decoder

    private readonly Dictionary<string, float> _piezasOrigen; // pieza -> puntuación (del .spm de origen)
    private readonly float _puntuacionMinima;
    private readonly Dictionary<string, int> _vocab;          // vocab.json: pieza -> id del modelo
    private readonly string[] _vocabInverso;                  // id -> pieza

    public TokenizadorMarian(string rutaSpmOrigen, string rutaVocabJson)
    {
        var piezas = LectorSpm.LeerPiezas(rutaSpmOrigen);
        _piezasOrigen = new Dictionary<string, float>();
        float min = 0f;
        foreach (var p in piezas)
        {
            _piezasOrigen[p.Texto] = p.Puntuacion;
            if (p.Tipo == 1 && p.Puntuacion < min) min = p.Puntuacion;
        }
        _puntuacionMinima = min;

        var json = File.ReadAllText(rutaVocabJson);
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                 ?? throw new InvalidOperationException("vocab.json ilegible");
        _vocabInverso = new string[_vocab.Count];
        foreach (var (pieza, id) in _vocab)
        {
            if (id >= 0 && id < _vocabInverso.Length) _vocabInverso[id] = pieza;
        }
    }

    // ---------- Preparación del texto (aproximación del normalizador de SentencePiece) ----------

    /// <summary>
    /// Normaliza (NFKC), colapsa espacios y escapa como hace SentencePiece:
    /// prefijo ficticio «▁» y cada espacio pasa a ser «▁» (U+2581).
    /// </summary>
    private static string Escapar(string texto)
    {
        string n = texto.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(n.Length + 1);
        sb.Append('\u2581');
        bool espacioPrevio = false;
        foreach (char c in n.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!espacioPrevio) sb.Append('\u2581');
                espacioPrevio = true;
            }
            else
            {
                sb.Append(c);
                espacioPrevio = false;
            }
        }
        return sb.ToString();
    }

    private static List<string> Caracteres(string texto)
    {
        // SentencePiece trabaja sobre caracteres Unicode (puntos de código).
        var lista = new List<string>();
        foreach (var runa in texto.EnumerateRunes())
        {
            lista.Add(runa.ToString());
        }
        return lista;
    }

    // ---------- Unigram: Viterbi maximizando la suma de puntuaciones ----------

    /// <summary>Trocea el texto en piezas del .spm de origen.</summary>
    public List<string> CodificarPiezas(string texto)
    {
        var chars = Caracteres(Escapar(texto));
        int n = chars.Count;
        if (n == 0) return new List<string>();
        const int maxTrozo = 24;
        var mejor = new double[n + 1];
        var desde = new int[n + 1];
        var piezaElegida = new string?[n + 1];
        for (int i = 1; i <= n; i++) mejor[i] = double.NegativeInfinity;
        mejor[0] = 0;

        for (int i = 0; i < n; i++)
        {
            if (double.IsNegativeInfinity(mejor[i])) continue;
            // Trozo desconocido de un carácter: permitido con penalización.
            double conUnk = mejor[i] + _puntuacionMinima - 10.0;
            if (conUnk > mejor[i + 1])
            {
                mejor[i + 1] = conUnk;
                desde[i + 1] = i;
                piezaElegida[i + 1] = "<unk>";
            }
            var sb = new StringBuilder();
            for (int len = 1; len <= maxTrozo && i + len <= n; len++)
            {
                sb.Append(chars[i + len - 1]);
                string trozo = sb.ToString();
                if (_piezasOrigen.TryGetValue(trozo, out float p))
                {
                    double cand = mejor[i] + p;
                    if (cand > mejor[i + len])
                    {
                        mejor[i + len] = cand;
                        desde[i + len] = i;
                        piezaElegida[i + len] = trozo;
                    }
                }
            }
        }

        var piezas = new List<string>();
        for (int pos = n; pos > 0; pos = desde[pos])
        {
            piezas.Add(piezaElegida[pos] ?? "<unk>");
        }
        piezas.Reverse();
        return piezas;
    }

    // ---------- Piezas <-> ids del modelo ----------

    /// <summary>Codifica a ids del modelo y añade &lt;/s&gt; (id 0) al final, como MarianTokenizer.</summary>
    public long[] CodificarIds(string texto)
    {
        var piezas = CodificarPiezas(texto);
        return PiezasAIds(piezas);
    }

    /// <summary>Convierte piezas ya troceadas a ids, con &lt;/s&gt; al final.</summary>
    public long[] PiezasAIds(IReadOnlyList<string> piezas)
    {
        var ids = new long[piezas.Count + 1];
        for (int i = 0; i < piezas.Count; i++)
        {
            ids[i] = _vocab.TryGetValue(piezas[i], out int id) ? id : IdUnk;
        }
        ids[^1] = IdEos;
        return ids;
    }

    /// <summary>Número de tokens (piezas) de un texto, sin contar el &lt;/s&gt; final.</summary>
    public int ContarTokens(string texto) => CodificarPiezas(texto).Count;

    /// <summary>Descodifica ids generados: salta especiales y convierte «▁» en espacios.</summary>
    public string Descodificar(IEnumerable<long> ids)
    {
        var sb = new StringBuilder();
        foreach (long id in ids)
        {
            if (id == IdEos || id == IdPad) continue;
            if (id < 0 || id >= _vocabInverso.Length) continue;
            string? pieza = _vocabInverso[id];
            if (pieza is null || pieza == "<unk>") continue;
            sb.Append(pieza);
        }
        return sb.ToString().Replace('\u2581', ' ').Trim();
    }
}
