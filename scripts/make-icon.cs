#:package SkiaSharp

// Genera src/Cus.UI/Assets/cus.ico, l'icona di CUS, nello stile della skin "lettore retrò"
// (src/Cus.UI/Skin.cs): telaio in metallo scuro, display LCD lime con clipboard e frecce ⇄,
// pulsanti tondi lucidi. Ogni dimensione è disegnata a parte: da 48 px con i pulsanti,
// 32-40 px solo telaio e display, 16-24 px in pixel reali con le sole frecce.
//
// Uso, dalla radice del repo:
//   dotnet run scripts/make-icon.cs                  # riscrive src/Cus.UI/Assets/cus.ico
//   dotnet run scripts/make-icon.cs -- --png <dir>   # scrive anche un PNG per dimensione, per controllarle
using SkiaSharp;

var scriptDir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var icoPath = Path.GetFullPath(Path.Combine(scriptDir, "..", "src", "Cus.UI", "Assets", "cus.ico"));
string? pngDir = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--png" && i + 1 < args.Length) pngDir = args[++i];
    else { Console.Error.WriteLine($"Argomento non riconosciuto: {args[i]}"); return 1; }
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = new List<(int Size, byte[] Data)>();
foreach (var size in sizes)
{
    var data = Render(size);
    pngs.Add((size, data));
    if (pngDir is not null)
    {
        Directory.CreateDirectory(pngDir);
        File.WriteAllBytes(Path.Combine(pngDir, $"cus-{size}.png"), data);
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
File.WriteAllBytes(icoPath, BuildIco(pngs));
Console.WriteLine($"Scritto {icoPath}");
return 0;

static SKColor C(string hex) => SKColor.Parse(hex);

static byte[] Render(int size)
{
    var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    if (size <= 24) RenderTiny(canvas, size); else RenderFull(canvas, size);
    using var image = surface.Snapshot();
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
}

static SKShader ChassisShader(float top, float bottom) => SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom),
    [C("#5B6168"), C("#3B3F44"), C("#2C2F33"), C("#3A3E43")], [0f, 0.08f, 0.55f, 1f], SKShaderTileMode.Clamp);

static SKShader LcdShader(float top, float bottom) => SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom),
    [C("#C8F55A"), C("#8ED31F"), C("#5FA20E")], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);

// 16-24 px: disegnata in pixel reali. Telaio con bordo di 1 px, display lime, solo le frecce.
static void RenderTiny(SKCanvas canvas, int size)
{
    float r = size / 5f;
    var chassis = new SKRect(0, 0, size, size);
    using (var p = new SKPaint { IsAntialias = true, Shader = ChassisShader(0, size) })
        canvas.DrawRoundRect(chassis, r, r, p);
    using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = C("#121417") })
        canvas.DrawRoundRect(Inset(chassis, 0.5f), r - 0.5f, r - 0.5f, p);

    var lcd = Inset(chassis, 2);
    float lr = r * 0.5f;
    using (var p = new SKPaint { IsAntialias = true, Shader = LcdShader(lcd.Top, lcd.Bottom) })
        canvas.DrawRoundRect(lcd, lr, lr, p);

    var ink = C("#11240A");
    float stroke = size <= 16 ? 1.5f : 2f;
    using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = ink, StrokeCap = SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Miter };
    float inner = lcd.Width;
    float pad = MathF.Round(inner * 0.16f);
    float x0 = lcd.Left + pad, x1 = lcd.Right - pad;
    float head = MathF.Round(inner * 0.24f);
    float y1 = lcd.Top + MathF.Round(inner * 0.32f) + (stroke % 2 == 0 ? 0 : 0.5f);
    float y2 = lcd.Bottom - MathF.Round(inner * 0.32f) - (stroke % 2 == 0 ? 0 : 0.5f);
    Arrow(canvas, line, new SKPoint(x0, y1), new SKPoint(x1, y1), head);
    Arrow(canvas, line, new SKPoint(x1, y2), new SKPoint(x0, y2), head);
}

static void RenderFull(SKCanvas canvas, int size)
{
    canvas.Scale(size / 256f);
    var detailed = size >= 48;
    float px = 256f / size; // un pixel reale in unità della griglia 256

    // --- Telaio ---
    float m = detailed ? 10 : 4;
    var chassis = new SKRect(m, m, 256 - m, 256 - m);
    float chassisR = 52;
    using (var p = new SKPaint { IsAntialias = true, Shader = ChassisShader(chassis.Top, chassis.Bottom) })
        canvas.DrawRoundRect(chassis, chassisR, chassisR, p);
    if (detailed)
    {
        // Smusso: luce in alto, ombra in basso.
        float bevelW = 6;
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = bevelW };
        var r = Inset(chassis, bevelW / 2 + 4);
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
            [C("#7A8088"), C("#1C1E21")], null, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(r, chassisR - 6, chassisR - 6, p);
    }
    float edgeW = Math.Max(8, px);
    using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = edgeW, Color = C("#121417") })
        canvas.DrawRoundRect(Inset(chassis, edgeW / 2), chassisR - edgeW / 2, chassisR - edgeW / 2, p);

    // --- Display LCD ---
    var lcd = detailed ? new SKRect(38, 36, 218, 168) : new SKRect(30, 30, 226, 226);
    float lcdR = detailed ? 20 : 26;
    using (var p = new SKPaint { IsAntialias = true, Shader = LcdShader(lcd.Top, lcd.Bottom) })
        canvas.DrawRoundRect(lcd, lcdR, lcdR, p);
    if (detailed)
    {
        // Ombra interna in alto, come il BoxShadow "inset" del display.
        using var p = new SKPaint { IsAntialias = true };
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, lcd.Top), new SKPoint(0, lcd.Top + lcd.Height * 0.22f),
            [new SKColor(0, 0, 0, 90), new SKColor(0, 0, 0, 0)], null, SKShaderTileMode.Clamp);
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(lcd, lcdR), antialias: true);
        canvas.DrawRect(lcd, p);
        canvas.Restore();
    }
    float lcdEdge = Math.Max(5, px);
    using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = lcdEdge, Color = C("#0C1505") })
        canvas.DrawRoundRect(Inset(lcd, lcdEdge / 2), lcdR - lcdEdge / 2, lcdR - lcdEdge / 2, p);

    DrawGlyph(canvas, Inset(lcd, lcdEdge), px, detailed);

    // --- Pulsanti tondi lucidi ---
    if (detailed)
    {
        DrawRoundButton(canvas, new SKPoint(96, 204), 20);
        DrawRoundButton(canvas, new SKPoint(160, 204), 20);
    }
}

// Clipboard con due frecce opposte: copia che va e torna tra i PC.
static void DrawGlyph(SKCanvas canvas, SKRect area, float px, bool detailed)
{
    var ink = C("#11240A");
    float h = area.Height;
    float stroke = Math.Max(h * (detailed ? 0.07f : 0.085f), 1.6f * px);
    float bh = h * 0.7f;
    float bw = bh * 0.86f;
    float cx = area.MidX;
    float top = area.Top + (h - bh) / 2 + h * 0.05f;
    var board = new SKRect(cx - bw / 2, top, cx + bw / 2, top + bh);

    using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = ink, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    using var fill = new SKPaint { IsAntialias = true, Color = ink };

    float br = bw * 0.14f;
    canvas.DrawRoundRect(Inset(board, stroke / 2), br, br, line);

    // Molla della clipboard, larga e a cavallo del bordo superiore, con il foro.
    float cw = bw * 0.6f, ch = bh * 0.2f;
    var clip = new SKRect(cx - cw / 2, board.Top - ch * 0.4f, cx + cw / 2, board.Top + ch * 0.6f);
    float cr = ch * 0.35f;
    canvas.DrawRoundRect(clip, cr, cr, fill);
    using (var hole = new SKPaint { IsAntialias = true, Color = C("#C8F55A") })
    {
        float hr = ch * 0.17f;
        canvas.DrawRoundRect(new SKRect(cx - hr * 2.2f, clip.Top + ch * 0.22f, cx + hr * 2.2f, clip.Top + ch * 0.22f + hr * 2), hr, hr, hole);
    }

    // Frecce: sopra verso destra, sotto verso sinistra.
    float aw = bw * 0.56f;
    float head = Math.Max(bh * 0.11f, 1.8f * px);
    float y1 = board.Top + bh * 0.5f;
    float y2 = board.Top + bh * 0.76f;
    float x0 = cx - aw / 2, x1 = cx + aw / 2;
    Arrow(canvas, line, new SKPoint(x0, y1), new SKPoint(x1, y1), head);
    Arrow(canvas, line, new SKPoint(x1, y2), new SKPoint(x0, y2), head);
}

static void Arrow(SKCanvas canvas, SKPaint paint, SKPoint from, SKPoint to, float head)
{
    float dir = Math.Sign(to.X - from.X);
    using var path = new SKPath();
    path.MoveTo(from);
    path.LineTo(to);
    path.MoveTo(to.X - dir * head, to.Y - head);
    path.LineTo(to);
    path.LineTo(to.X - dir * head, to.Y + head);
    canvas.DrawPath(path, paint);
}

// Come lo stile Button.round: anello scuro, sfera lime con gradiente radiale, riflesso in alto.
static void DrawRoundButton(SKCanvas canvas, SKPoint c, float r)
{
    using (var p = new SKPaint { IsAntialias = true, Color = C("#1B1D20") })
        canvas.DrawCircle(c, r + 5, p);
    var box = new SKRect(c.X - r, c.Y - r, c.X + r, c.Y + r);
    using (var p = new SKPaint { IsAntialias = true })
    {
        var origin = new SKPoint(box.Left + box.Width * 0.38f, box.Top + box.Height * 0.3f);
        var center = new SKPoint(box.Left + box.Width * 0.45f, box.Top + box.Height * 0.4f);
        p.Shader = SKShader.CreateTwoPointConicalGradient(origin, 0, center, r * 1.1f,
            [C("#EAFFA0"), C("#A6EE2C"), C("#5EA40C"), C("#2F5C03")], [0f, 0.35f, 0.8f, 1f], SKShaderTileMode.Clamp);
        canvas.DrawCircle(c, r, p);
    }
    using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = C("#0E1A04") })
        canvas.DrawCircle(c, r, p);
    var hl = new SKRect(box.Left + r * 0.32f, box.Top + r * 0.16f, box.Right - r * 0.32f, box.Top + r * 0.95f);
    using (var p = new SKPaint { IsAntialias = true })
    {
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, hl.Top), new SKPoint(0, hl.Bottom),
            [new SKColor(255, 255, 255, 170), new SKColor(255, 255, 255, 0)], null, SKShaderTileMode.Clamp);
        canvas.DrawOval(hl, p);
    }
}

static SKRect Inset(SKRect r, float d) => new(r.Left + d, r.Top + d, r.Right - d, r.Bottom - d);

// ICO con voci PNG (supportate da Windows Vista in poi per tutte le dimensioni).
static byte[] BuildIco(List<(int Size, byte[] Data)> images)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
    w.Flush();
    return ms.ToArray();
}
