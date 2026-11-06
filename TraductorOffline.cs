using System.IO;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TraductorPantalla;

/// <summary>Se lanza cuando la carpeta de un modelo no está junto al exe.</summary>
public sealed class ModeloAusenteException(string direccion)
    : Exception($"Falta el modelo {direccion}: no está en la carpeta modelos")
{
    public string Direccion { get; } = direccion;
}

/// <summary>
/// Traducción 100% local con modelos Opus-MT (Helsinki-NLP) en formato ONNX
/// ejecutados con ONNX Runtime. Sin red, sin claves, sin servicios externos.
///
/// Cada dirección («en-es», «es-en») es una carpeta en `modelos/` junto al
/// exe con cinco archivos: encoder_model_quantized.onnx,
/// decoder_model_quantized.onnx, decoder_with_past_model_quantized.onnx,
/// source.spm y vocab.json.
///
/// La inferencia es la comprobada en la prueba de humo (prueba-onnx):
/// el encoder corre una vez; el decoder completo (sin caché) genera el
/// primer token y siembra la caché de claves/valores —incluidas las del
/// encoder, ya proyectadas—; decoder_with_past genera el resto reutilizando
/// esa caché (que se actualiza, no se sustituye: el decoder con caché no
/// devuelve las claves/valores del encoder porque no cambian).
/// El decoder «merged» de los mismos repos queda descartado: su grafo con
/// un nodo If revienta en ONNX Runtime nativo (Reshape de encoder_attn).
/// </summary>
public static class TraductorOffline
{
    /// <summary>Archivos que el motor necesita dentro de modelos/&lt;dirección&gt;/.</summary>
    private static readonly string[] ArchivosModelo =
    {
        "encoder_model_quantized.onnx",
        "decoder_model_quantized.onnx",
        "decoder_with_past_model_quantized.onnx",
        "source.spm",
        "vocab.json",
    };

    // Una sola inferencia a la vez: las sesiones ONNX de un par no se comparten entre hebras.
    private static readonly SemaphoreSlim Semaforo = new(1, 1);
    private static readonly Dictionary<string, MotorPar> Motores = new();
    private static readonly Dictionary<string, string> Cache = new();
    private static readonly object Candado = new();

    /// <summary>Carpeta de un modelo, p. ej. …\modelos\en-es.</summary>
    public static string CarpetaDe(string direccion) =>
        Path.Combine(AppContext.BaseDirectory, "modelos", direccion);

    /// <summary>True si la carpeta del modelo existe con todos sus archivos.</summary>
    public static bool ModeloDisponible(string direccion)
    {
        string carpeta = CarpetaDe(direccion);
        return ArchivosModelo.All(f => File.Exists(Path.Combine(carpeta, f)));
    }

    /// <summary>
    /// Traduce una lista de bloques en la dirección dada («en-es» o «es-en»).
    /// Lanza <see cref="ModeloAusenteException"/> si falta la carpeta del modelo.
    /// </summary>
    public static async Task<string[]> TraducirBloquesAsync(IReadOnlyList<string> textos, string direccion)
    {
        if (textos.Count == 0) return Array.Empty<string>();
        if (!ModeloDisponible(direccion)) throw new ModeloAusenteException(direccion);

        var resultados = new string[textos.Count];
        await Semaforo.WaitAsync();
        try
        {
            var motor = ObtenerMotor(direccion);
            for (int i = 0; i < textos.Count; i++)
            {
                string clave = direccion + "\0" + textos[i];
                lock (Candado)
                {
                    if (Cache.TryGetValue(clave, out var guardado))
                    {
                        resultados[i] = guardado;
                        continue;
                    }
                }

                // La inferencia es CPU pura: fuera del hilo de la UI.
                string texto = textos[i];
                string traducido = await Task.Run(() => motor.Traducir(texto));
                resultados[i] = traducido;
                lock (Candado) Cache[clave] = traducido;
            }
        }
        finally
        {
            Semaforo.Release();
        }
        return resultados;
    }

    private static MotorPar ObtenerMotor(string direccion)
    {
        lock (Candado)
        {
            if (Motores.TryGetValue(direccion, out var motor)) return motor;
            motor = new MotorPar(CarpetaDe(direccion));
            Motores[direccion] = motor;
            return motor;
        }
    }

    /// <summary>Las sesiones y el tokenizador de un par de idiomas.</summary>
    private sealed class MotorPar : IDisposable
    {
        // El encoder admite hasta 512 posiciones: los trozos se cortan a 480 tokens.
        private const int MaxTokensPorTrozo = 480;
        private const int MaxTokensGenerados = 256;

        private readonly TokenizadorMarian _tokenizador;
        private readonly InferenceSession _encoder;
        private readonly InferenceSession _decoderSinCache;
        private readonly InferenceSession _decoderConCache;
        private readonly List<string> _nombresPasado;

        public MotorPar(string carpeta)
        {
            _tokenizador = new TokenizadorMarian(
                Path.Combine(carpeta, "source.spm"),
                Path.Combine(carpeta, "vocab.json"));
            _encoder = new InferenceSession(Path.Combine(carpeta, "encoder_model_quantized.onnx"));
            _decoderSinCache = new InferenceSession(Path.Combine(carpeta, "decoder_model_quantized.onnx"));
            _decoderConCache = new InferenceSession(Path.Combine(carpeta, "decoder_with_past_model_quantized.onnx"));
            _nombresPasado = _decoderConCache.InputMetadata.Keys
                .Where(n => n.StartsWith("past_key_values."))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Traduce un bloque; si pasa del límite del encoder, lo trocea por frases.</summary>
        public string Traducir(string texto)
        {
            texto = texto.Trim();
            if (texto.Length == 0) return "";
            var trozos = Trocear(texto);
            return string.Join(" ", trozos.Select(TraducirTrozo)).Trim();
        }

        /// <summary>Parte el texto en trozos de como mucho MaxTokensPorTrozo tokens.</summary>
        private List<string> Trocear(string texto)
        {
            if (_tokenizador.ContarTokens(texto) <= MaxTokensPorTrozo)
            {
                return new List<string> { texto };
            }

            // Frases sueltas (con su puntuación) empaquetadas sin pasar del límite.
            var frases = Regex.Split(texto, @"(?<=[.!?;])\s+")
                              .Where(f => f.Length > 0)
                              .ToList();
            var trozos = new List<string>();
            var actual = new List<string>();
            int tokensActual = 0;
            foreach (string frase in frases)
            {
                int tokensFrase = _tokenizador.ContarTokens(frase);
                if (tokensFrase > MaxTokensPorTrozo)
                {
                    // Frase gigante: primero se vuelca lo acumulado y luego
                    // se parte por palabras a la fuerza.
                    if (actual.Count > 0)
                    {
                        trozos.Add(string.Join(" ", actual));
                        actual = new List<string>();
                        tokensActual = 0;
                    }
                    trozos.AddRange(PartirPorPalabras(frase));
                    continue;
                }
                if (tokensActual + tokensFrase > MaxTokensPorTrozo && actual.Count > 0)
                {
                    trozos.Add(string.Join(" ", actual));
                    actual = new List<string>();
                    tokensActual = 0;
                }
                actual.Add(frase);
                tokensActual += tokensFrase;
            }
            if (actual.Count > 0) trozos.Add(string.Join(" ", actual));
            return trozos;
        }

        private List<string> PartirPorPalabras(string frase)
        {
            var trozos = new List<string>();
            var actual = new List<string>();
            foreach (string palabra in frase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                actual.Add(palabra);
                if (_tokenizador.ContarTokens(string.Join(" ", actual)) >= MaxTokensPorTrozo)
                {
                    trozos.Add(string.Join(" ", actual));
                    actual = new List<string>();
                }
            }
            if (actual.Count > 0) trozos.Add(string.Join(" ", actual));
            return trozos;
        }

        /// <summary>Generación voraz de un trozo con la caché KV híbrida.</summary>
        private string TraducirTrozo(string trozo)
        {
            long[] idsOrigen = _tokenizador.CodificarIds(trozo);
            int seq = idsOrigen.Length;

            // --- Encoder, una sola vez ---
            var tensorIds = new DenseTensor<long>(idsOrigen, new[] { 1, seq });
            var mascara = new DenseTensor<long>(Enumerable.Repeat(1L, seq).ToArray(), new[] { 1, seq });
            using var resEnc = _encoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", tensorIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mascara),
            });
            var ocultosTensor = resEnc.First(v => v.Name == "last_hidden_state").AsTensor<float>();
            var ocultos = new DenseTensor<float>(ocultosTensor.ToArray(), ocultosTensor.Dimensions.ToArray());

            // --- Paso 0: decoder completo; su present.* siembra la caché ---
            var pasado = new Dictionary<string, DenseTensor<float>>();
            long mejorId;
            {
                var tensorEntrada = new DenseTensor<long>(new long[] { TokenizadorMarian.IdPad }, new[] { 1, 1 });
                using var res = _decoderSinCache.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", tensorEntrada),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mascara),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", ocultos),
                });
                var logits = res.First(v => v.Name == "logits").AsTensor<float>();
                mejorId = ArgmaxLogits(logits, 0);
                foreach (var valor in res)
                {
                    if (valor.Name.StartsWith("present."))
                    {
                        string clave = "past_key_values." + valor.Name["present.".Length..];
                        var t = valor.AsTensor<float>();
                        pasado[clave] = new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());
                    }
                }
            }

            // --- Pasos siguientes: decoder con caché ---
            var generados = new List<long>();
            for (int paso = 0; paso < MaxTokensGenerados; paso++)
            {
                if (mejorId == TokenizadorMarian.IdEos) break;
                generados.Add(mejorId);

                var entradas = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids",
                        new DenseTensor<long>(new[] { mejorId }, new[] { 1, 1 })),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mascara),
                };
                foreach (string nombre in _nombresPasado)
                {
                    entradas.Add(NamedOnnxValue.CreateFromTensor(nombre, pasado[nombre]));
                }

                using var resDec = _decoderConCache.Run(entradas);
                var logits = resDec.First(v => v.Name == "logits").AsTensor<float>();
                mejorId = ArgmaxLogits(logits, 0);

                // Actualizar, no sustituir: las claves/valores del encoder
                // no se devuelven porque no cambian entre pasos.
                foreach (var valor in resDec)
                {
                    if (valor.Name.StartsWith("present."))
                    {
                        string clave = "past_key_values." + valor.Name["present.".Length..];
                        var t = valor.AsTensor<float>();
                        pasado[clave] = new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());
                    }
                }
            }

            return _tokenizador.Descodificar(generados);
        }

        private static long ArgmaxLogits(Tensor<float> logits, int posicion)
        {
            int vocab = logits.Dimensions[2];
            long mejorId = -1;
            float mejorValor = float.NegativeInfinity;
            for (int v = 0; v < vocab; v++)
            {
                // <pad> está prohibido como salida (bad_words_ids de la config del modelo).
                if (v == TokenizadorMarian.IdPad) continue;
                float valor = logits[0, posicion, v];
                if (valor > mejorValor) { mejorValor = valor; mejorId = v; }
            }
            return mejorId;
        }

        public void Dispose()
        {
            _encoder.Dispose();
            _decoderSinCache.Dispose();
            _decoderConCache.Dispose();
        }
    }
}
