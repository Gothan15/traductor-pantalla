# Traductor de pantalla

Un programa para Windows que traduce **todo el texto que se ve en la pantalla**
y lo sustituye en su misma posición, como hace el traductor del navegador con
las páginas web: lees la pantalla ya traducida, sin copiar ni pegar nada.

**Todo funciona sin conexión**: el reconocimiento de texto y la traducción
corren en tu PC con modelos locales. El programa no hace ninguna llamada de
red.

## Cómo se usa

1. Abre `TraductorPantalla.exe`. Aparece una ventana pequeña de control.
   Junto al exe tienen que estar sus DLL y la carpeta `modelos` tal como
   salen de la publicación (no muevas el exe solo a otra carpeta).
2. Elige la **dirección de traducción**: «Inglés → Español» (por defecto) o
   «Español → Inglés».
3. Pulsa **«Traducir pantalla»** o el atajo global **`Ctrl+Alt+T`**.
   La ventana de control se oculta un instante, se captura la pantalla, se
   reconoce el texto, se traduce y aparece la traducción pintada encima de
   cada texto, en su sitio.
4. Para quitar la capa: **`Ctrl+Alt+T`** otra vez, la tecla **`Esc`** o el
   botón **«Quitar traducción»**.
5. La capa es transparente a los clics (*click-through*): puedes seguir
   trabajando en la aplicación de debajo como si no estuviera.

Si falta la carpeta del modelo de la dirección elegida, la ventana lo dice
(«Falta el modelo …: no está en la carpeta modelos») y no se muestra capa.

## Cómo funciona por dentro

- **Captura** de cada monitor con GDI (BitBlt), en píxeles físicos reales.
- **OCR local** con el motor integrado de Windows (`Windows.Media.Ocr`):
  el texto se reconoce en tu PC, sin conexión y sin claves.
- Las líneas reconocidas se **agrupan en bloques** (párrafos) y se traducen
  **en local** con un modelo neuronal **Opus-MT** ejecutado con
  **ONNX Runtime**: tokenizador SentencePiece (unigram) propio, encoder una
  vez por bloque y decoder voraz con caché de claves/valores.
- Cada bloque traducido se pinta en una **ventana transparente por monitor**
  que tapa el texto original con un recuadro del color de fondo muestreado
  alrededor del texto, y escribe la traducción encima con un tamaño acorde.

## Los modelos

- Son los **Opus-MT de Helsinki-NLP** (los mismos que usan Argos Translate
  y LibreTranslate), en su exportación ONNX cuantizada (int8) de los
  repositorios `Xenova/opus-mt-en-es` y `Xenova/opus-mt-es-en` de
  HuggingFace. Licencia de los modelos: **CC-BY 4.0**. ONNX Runtime es MIT.
- Direcciones incluidas en esta versión: **inglés → español** y
  **español → inglés**. El selector solo ofrece esas dos.
- Cada dirección vive en `modelos/<dirección>/` junto al exe, con los cinco
  archivos que el motor usa: `encoder_model_quantized.onnx`,
  `decoder_model_quantized.onnx`, `decoder_with_past_model_quantized.onnx`,
  `source.spm` y `vocab.json` — unos **164 MB por dirección**.
- El tamaño total de la carpeta publicada ronda los **530 MB** (el exe
  autocontenido, las DLL nativas y los dos modelos).

## Límites honestos

- Ningún programa puede reescribir lo que otra aplicación dibuja: el efecto
  se logra **pintando encima**, con el fondo muestreado. Si el fondo es una
  foto o un degradado complejo, el recuadro se notará.
- La traducción es una **foto fija** de la pantalla: si el contenido se mueve
  (vídeo, scroll, juegos), hay que retraducir con el atajo.
- Los juegos en **pantalla completa exclusiva** pueden salir negros en la
  captura; en modo ventana o ventana sin bordes funciona.
- Solo traduce entre los idiomas incluidos (inglés y español). La calidad
  es la de Opus-MT: buena en texto general, más floja en jerga muy técnica.
- La primera traducción tras abrir el programa tarda un poco más: hay que
  cargar el modelo de la dirección elegida (después queda en memoria, con
  caché de textos ya traducidos).
- La calidad del resultado depende también del OCR de Windows y del idioma
  instalado; el texto muy pequeño o con poco contraste se reconoce peor.

## Compilación

Con el SDK de .NET 10 instalado:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

Eso deja en `dist` el `TraductorPantalla.exe` autocontenido, las DLL
nativas (entre ellas `onnxruntime.dll`) y la carpeta `modelos` con los dos
pares. También puedes abrir la carpeta en Visual Studio y compilar el
proyecto `TraductorPantalla.csproj` directamente.

La carpeta `prueba-onnx` es la prueba de humo con la que se validó el motor
offline en Linux (tokenización unigram + inferencia ONNX de los dos pares);
no forma parte de la aplicación.
