// SPCleaner+ ikon ureteci - Windows 11 (Fluent) tarzi ikon cizer, cok boyutlu .ico yazar.
// Secilen tasarim: "harf" (buyuk SP diskin ustunde, onde supurge, yaninda isiltilar).
// Yeniden uretmek: ikon\ikon_olustur.bat  (proje kokune SPCleaner+.ico yazar, sonra build.bat)
// Elle: IkonOlustur.exe <cikti.ico> [onizleme_klasoru] [kare|harf|sadece]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

internal static class IkonOlustur
{
    private static readonly int[] Sizes = { 256, 64, 48, 40, 32, 24, 20, 16 };
    // kare: SP mavi karede | harf: SP harfleri arka plansiz + disk/supurge | sadece: yalniz SP harfleri
    private static string Mode = "harf";

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("Kullanim: IkonOlustur <cikti.ico> [onizleme_klasoru]"); return 1; }
        string preview = args.Length > 1 ? args[1] : null;
        if (args.Length > 2) Mode = args[2].ToLowerInvariant();
        var pngs = new List<byte[]>();
        foreach (int s in Sizes)
        {
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
                if (preview != null) bmp.Save(Path.Combine(preview, "ikon_" + Mode + "_" + s + ".png"), ImageFormat.Png);
            }
        }
        WriteIco(args[0], pngs);
        Console.WriteLine("Yazildi: " + args[0]);
        return 0;
    }

    // ---- cizim (256'lik koordinat sisteminde) ----
    // Cizim once 256'lik alanda yapilir, sonra gorunen icerigin siniri olculup ikon karesini
    // kenardan kenara dolduracak sekilde buyutulur (masaustunde diger ikonlar kadar buyuk gorunsun).
    private const float Margin = 3f;

    private static Bitmap Render(int size)
    {
        bool small = size <= 24;
        RectangleF c = ContentBounds(small);
        float k = Math.Min((256 - 2 * Margin) / c.Width, (256 - 2 * Margin) / c.Height);
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Setup(g);
            g.ScaleTransform(size / 256f, size / 256f);
            g.TranslateTransform((256 - c.Width * k) / 2 - c.X * k, (256 - c.Height * k) / 2 - c.Y * k);
            g.ScaleTransform(k, k);
            DrawScene(g, small);
        }
        return bmp;
    }

    private static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
    }

    private static void DrawScene(Graphics g, bool small)
    {
        if (Mode == "sadece") DrawLetters(g, new RectangleF(0, 0, 256, 256), 168f, 14f);
        else if (small) { if (Mode == "harf") DrawLetters(g, new RectangleF(0, 0, 256, 256), 168f, 22f); else DrawSmall(g); }
        else DrawFull(g);
    }

    // gorunen (yari saydam golge haric) icerigin 256'lik alandaki siniri
    private static RectangleF ContentBounds(bool small)
    {
        const int S = 512, pad = 128;   // tasmalar da olculsun diye kenar payli tuval
        using (var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.TranslateTransform(pad, pad);
                DrawScene(g, small);
            }
            int minX = S, minY = S, maxX = -1, maxY = -1;
            var data = bmp.LockBits(new Rectangle(0, 0, S, S), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * S];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            int stride = data.Stride;
            bmp.UnlockBits(data);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    if (px[y * stride + x * 4 + 3] > 40)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return new RectangleF(0, 0, 256, 256);
            return new RectangleF(minX - pad, minY - pad, maxX - minX + 1, maxY - minY + 1);
        }
    }

    private static void DrawFull(Graphics g)
    {
        if (Mode == "harf")
        {
            // SP buyuk ve diskin ustune oturuyor; supurge onde, P'nin bos sag altini supuruyor
            DrawDisk(g, new RectangleF(22, 168, 212, 66));
            DrawLetters(g, new RectangleF(4, 0, 214, 173), 150f, 10f, true, 22f); // sol kenar = diskin sol kenari
            DrawBroom(g, 212, 108, 24f, 0.75f);
            DrawSparkle(g, 240, 150, 12, Color.FromArgb(0x2B, 0x9C, 0xF0));
            DrawSparkle(g, 245, 124, 7, Color.FromArgb(0x7F, 0xD0, 0xFF));
            return;
        }
        DrawTile(g, new RectangleF(16, 14, 140, 140), 30, 70f);
        DrawDisk(g, new RectangleF(22, 168, 212, 66));
        DrawBroom(g, 170, 96, 36f, 1f);
        DrawSparkle(g, 198, 148, 16, Color.FromArgb(0x2B, 0x9C, 0xF0));
        DrawSparkle(g, 226, 124, 9, Color.FromArgb(0x7F, 0xD0, 0xFF));
    }

    // kucuk boyutlarda (16-24 px) sadece SP karesi: detay okunmaz
    private static void DrawSmall(Graphics g)
    {
        DrawTile(g, new RectangleF(8, 8, 240, 240), 52, 124f);
    }

    // arka plansiz SP harfleri: mavi gecis dolgu + ince acik kontur (koyu zeminde de okunsun)
    private static void DrawLetters(Graphics g, RectangleF r, float fontPx, float stroke, bool sitOnBottom = false, float alignLeft = -1f, float scaleX = 1f)
    {
        using (var ff = PickFont())
        using (var text = new GraphicsPath())
        {
            text.AddString("SP", ff, (int)FontStyle.Bold, fontPx, new PointF(0, 0), StringFormat.GenericTypographic);
            if (scaleX != 1f) using (var sx = new Matrix()) { sx.Scale(scaleX, 1f); text.Transform(sx); }
            var b = text.GetBounds();
            using (var m = new Matrix())
            {
                m.Translate(alignLeft >= 0 ? alignLeft + stroke / 2 - b.X : r.X + (r.Width - b.Width) / 2 - b.X, sitOnBottom ? r.Bottom - b.Bottom : r.Y + (r.Height - b.Height) / 2 - b.Y);
                text.Transform(m);
            }
            b = text.GetBounds();
            Shadow(g, (GraphicsPath)text.Clone(), 5, 10);
            using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), stroke) { LineJoin = LineJoin.Round })
                g.DrawPath(pen, text);
            using (var br = new LinearGradientBrush(new RectangleF(b.X, b.Y - 1, b.Width, b.Height + 2), Color.FromArgb(0x38, 0xA8, 0xF8), Color.FromArgb(0x0A, 0x4C, 0xA8), 90f))
                g.FillPath(br, text);
        }
    }

    private static void DrawTile(Graphics g, RectangleF r, float radius, float fontPx)
    {
        Shadow(g, RoundRect(r, radius), 6, 10);
        using (var path = RoundRect(r, radius))
        using (var br = new LinearGradientBrush(r, Color.FromArgb(0x4C, 0xC2, 0xFF), Color.FromArgb(0x0F, 0x5C, 0xC0), 55f))
        {
            g.FillPath(br, path);
            // ust kenarda hafif parlama
            var top = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.5f);
            using (var gl = new LinearGradientBrush(top, Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            {
                var old = g.Clip; g.SetClip(path, CombineMode.Intersect);
                g.FillRectangle(gl, top);
                g.Clip = old;
            }
            using (var pen = new Pen(Color.FromArgb(60, 0, 40, 110), 2f)) g.DrawPath(pen, path);
        }
        using (var ff = PickFont())
        using (var font = new Font(ff, fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            var tr = new RectangleF(r.X, r.Y + r.Height * 0.03f, r.Width, r.Height);
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 30, 90)))
                g.DrawString("SP", font, sh, new RectangleF(tr.X + 2, tr.Y + 4, tr.Width, tr.Height), sf);
            g.DrawString("SP", font, Brushes.White, tr, sf);
        }
    }

    private static void DrawDisk(Graphics g, RectangleF body)
    {
        float h = body.Height;
        Shadow(g, RoundRect(body, 18), 5, 12);
        using (var path = RoundRect(body, 18))
        using (var br = new LinearGradientBrush(body, Color.FromArgb(0xF7, 0xF9, 0xFB), Color.FromArgb(0xB4, 0xBE, 0xC9), 90f))
        {
            g.FillPath(br, path);
            using (var pen = new Pen(Color.FromArgb(90, 60, 72, 86), 2f)) g.DrawPath(pen, path);
        }
        // on panel ayrimi
        float lineY = body.Y + h * 0.33f;
        using (var pen = new Pen(Color.FromArgb(70, 70, 82, 96), 2f)) g.DrawLine(pen, body.X + 18, lineY, body.Right - 18, lineY);
        // havalandirma yarigi + yesil LED, alt panelin ortasinda
        float midY = lineY + (body.Bottom - lineY) / 2;
        using (var slot = RoundRect(new RectangleF(body.X + 20, midY - 5, body.Width * 0.49f, 10), 5))
        using (var br = new SolidBrush(Color.FromArgb(0x8C, 0x98, 0xA5))) g.FillPath(br, slot);
        float lx = body.Right - 31;
        using (var glow = new SolidBrush(Color.FromArgb(70, 0x16, 0xC6, 0x0C))) g.FillEllipse(glow, lx - 11, midY - 11, 22, 22);
        using (var led = new SolidBrush(Color.FromArgb(0x16, 0xC6, 0x0C))) g.FillEllipse(led, lx - 6, midY - 6, 12, 12);
        using (var hl = new SolidBrush(Color.FromArgb(180, 255, 255, 255))) g.FillEllipse(hl, lx - 3, midY - 4, 5, 4);
    }

    private static void DrawBroom(Graphics g, float x, float y, float angle, float scale)
    {
        var state = g.Save();
        // supurge yerel koordinat: (0,0) sap ile kil baglantisi, sap -y yonunde, killar +y yonunde
        g.TranslateTransform(x, y);
        g.RotateTransform(angle);
        g.ScaleTransform(scale, scale);

        // sap
        var handle = new RectangleF(-7, -100, 14, 106);
        Shadow(g, RoundRect(handle, 7), 4, 6);
        using (var path = RoundRect(handle, 7))
        using (var br = new LinearGradientBrush(handle, Color.FromArgb(0xD9, 0x95, 0x5B), Color.FromArgb(0x8E, 0x55, 0x2A), 0f))
            g.FillPath(br, path);
        // sap ucu
        using (var cap = new SolidBrush(Color.FromArgb(0x5C, 0x6B, 0x7A))) g.FillEllipse(cap, -8, -106, 16, 16);

        // killar (yelpaze)
        var bristle = new GraphicsPath();
        bristle.AddLine(-22, 10, 22, 10);
        bristle.AddBezier(22, 10, 34, 34, 46, 58, 50, 84);
        // dalgali uc
        bristle.AddBezier(50, 84, 40, 76, 34, 90, 25, 82);
        bristle.AddBezier(25, 82, 16, 92, 8, 78, 0, 88);
        bristle.AddBezier(0, 88, -8, 78, -16, 92, -25, 82);
        bristle.AddBezier(-25, 82, -34, 90, -40, 76, -50, 84);
        bristle.AddBezier(-50, 84, -46, 58, -34, 34, -22, 10);
        bristle.CloseFigure();
        Shadow(g, (GraphicsPath)bristle.Clone(), 5, 10);
        var bb = new RectangleF(-50, 10, 100, 82);
        using (var br = new LinearGradientBrush(bb, Color.FromArgb(0xFF, 0xD8, 0x5A), Color.FromArgb(0xF2, 0x9A, 0x16), 90f))
            g.FillPath(br, bristle);
        // kil cizgileri
        using (var pen = new Pen(Color.FromArgb(90, 0xB8, 0x6A, 0x00), 2.2f))
        {
            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
            g.DrawLine(pen, -8, 28, -16, 76);
            g.DrawLine(pen, 8, 28, 16, 76);
            g.DrawLine(pen, -22, 30, -36, 74);
            g.DrawLine(pen, 22, 30, 36, 74);
            g.DrawLine(pen, 0, 28, 0, 80);
        }
        bristle.Dispose();

        // baglanti bandi (kirmizi, Fluent kirmizisi)
        var band = new RectangleF(-25, 2, 50, 20);
        using (var path = RoundRect(band, 7))
        using (var br = new LinearGradientBrush(band, Color.FromArgb(0xF0, 0x5A, 0x5E), Color.FromArgb(0xB0, 0x25, 0x2A), 90f))
            g.FillPath(br, path);
        using (var hl = new Pen(Color.FromArgb(110, 255, 255, 255), 2f)) g.DrawLine(hl, -18, 7, 18, 7);

        g.Restore(state);
    }

    // 4 koseli Fluent "isilti"
    private static void DrawSparkle(Graphics g, float cx, float cy, float r, Color c)
    {
        float k = r * 0.22f;
        var p = new GraphicsPath();
        p.AddBezier(cx, cy - r, cx + k * 0.4f, cy - k, cx + k, cy - k * 0.4f, cx + r, cy);
        p.AddBezier(cx + r, cy, cx + k, cy + k * 0.4f, cx + k * 0.4f, cy + k, cx, cy + r);
        p.AddBezier(cx, cy + r, cx - k * 0.4f, cy + k, cx - k, cy + k * 0.4f, cx - r, cy);
        p.AddBezier(cx - r, cy, cx - k, cy - k * 0.4f, cx - k * 0.4f, cy - k, cx, cy - r);
        p.CloseFigure();
        using (var glow = new Pen(Color.FromArgb(90, 0x4C, 0xC2, 0xFF), 3f)) g.DrawPath(glow, p);
        using (var br = new SolidBrush(c)) g.FillPath(br, p);
        p.Dispose();
    }

    // yumusak gölge: sekli asagi kaydirip artan kalinlikta seffaf kalemle birkac kez cizer
    private static void Shadow(Graphics g, GraphicsPath path, float dy, float blur)
    {
        var state = g.Save();
        g.TranslateTransform(0, dy);
        int steps = 6;
        for (int i = steps; i >= 1; i--)
        {
            float w = blur * i / steps;
            using (var pen = new Pen(Color.FromArgb(10, 0, 0, 0), w) { LineJoin = LineJoin.Round })
                g.DrawPath(pen, path);
        }
        using (var br = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillPath(br, path);
        g.Restore(state);
        path.Dispose();
    }

    private static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static FontFamily PickFont()
    {
        foreach (var name in new[] { "Segoe UI Variable Display", "Segoe UI" })
        {
            try { var ff = new FontFamily(name); if (ff.IsStyleAvailable(FontStyle.Bold)) return ff; ff.Dispose(); }
            catch { }
        }
        return new FontFamily(GenericFontFamilies.SansSerif);
    }

    // PNG sikistirmali girdilerle cok boyutlu ICO (Windows Vista+)
    private static void WriteIco(string file, List<byte[]> pngs)
    {
        using (var fs = File.Create(file))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
            int offset = 6 + 16 * pngs.Count;
            for (int i = 0; i < pngs.Count; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length);
                w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }
}
