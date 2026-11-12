// Generate mouse.ico from mouse_icon_source.png (transparent background, no frame).
// Source: 742x742 RGBA with the mouse cut out; regenerate when the artwork changes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class CreateIco {
    static Bitmap LoadFit(string path, int size) {
        using (var src = new Bitmap(path)) {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) {
                g.Clear(Color.Transparent);
                // SourceCopy keeps the transparent background instead of blending it.
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }
    }

    static void Main(string[] args) {
        string outPath = args.Length > 0 ? args[0] : "mouse.ico";
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        string srcPath = args.Length > 1 ? args[1] : Path.Combine(baseDir, "mouse_icon_source.png");
        if (!File.Exists(srcPath)) {
            Console.WriteLine("missing source: " + srcPath);
            return;
        }

        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        var imgs = new List<Bitmap>();
        foreach (int s in sizes) imgs.Add(LoadFit(srcPath, s));

        using (var fs = new FileStream(outPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs)) {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            var pngs = new List<byte[]>();
            foreach (var img in imgs) {
                using (var ms = new MemoryStream()) {
                    img.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            for (int i = 0; i < sizes.Length; i++) {
                int s = sizes[i];
                // 0 means 256 in the ICO directory.
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(pngs[i].Length);
                bw.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) bw.Write(p);
        }
        foreach (var img in imgs) img.Dispose();
        Console.WriteLine("saved " + outPath + " (16/20/24/32/40/48/64/96/128/256, transparent)");
    }
}
