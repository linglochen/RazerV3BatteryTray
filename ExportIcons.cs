// 导出全部 12 样式 × 5 电量的渲染预览（含充电动画两相位）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

class ExportIcons {
    static void Main(string[] args) {
        string outDir = args.Length > 0 ? args[0] : ".";
        var ranges = new List<ColorRange> {
            new ColorRange { Min = 20, Max = 100, ColorHex = "#00E676" },
            new ColorRange { Min = 10, Max = 20,  ColorHex = "#FF9800" },
            new ColorRange { Min = 0,  Max = 10,  ColorHex = "#F44336" },
        };
        string[] styles = { "plain", "ring", "mouse", "batH", "batV",
                            "barH", "barV", "bolt", "circle", "gauge", "hex", "drop" };
        string[] names  = { "纯文字", "圆环", "鼠标", "电池横", "电池竖",
                            "条横", "条竖", "闪电", "圆水位", "仪表", "六边", "水滴" };
        int[] pcts = { 100, 67, 44, 15, 5 };

        int cell = 40, pad = 8;
        int cols = pcts.Length * 2 + 1;
        int rows = styles.Length + 1;
        // 中灰底：黑描边电池和白描边圆环都能看清（深浅任务栏都能对比）
        using (var board = new Bitmap(cols * (cell + pad) + pad, rows * (cell + pad) + pad, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(board)) {
                g.Clear(Color.FromArgb(122, 124, 130));
                for (int r = 0; r < styles.Length; r++) {
                    for (int c = 0; c < pcts.Length; c++) {
                        int x = pad + c * 2 * (cell + pad), y = pad + (r + 1) * (cell + pad);
                        var o1 = new RenderOpts { Pct = pcts[c], Style = styles[r], Thick = 5, FontScale = 100 };
                        using (var a = IconRenderer.Render(32, o1, ranges)) g.DrawImage(a, x, y);
                        var o2 = new RenderOpts { Pct = pcts[c], Style = styles[r], Thick = 5, Charging = true, AnimPhase = 6 };
                        using (var b = IconRenderer.Render(32, o2, ranges)) g.DrawImage(b, x + cell + pad, y);
                    }
                }
                using (var font = new Font("Segoe UI", 9)) {
                    for (int c = 0; c < pcts.Length; c++) {
                        g.DrawString(pcts[c] + "%", font, Brushes.Black, pad + c * 2 * (cell + pad), pad);
                        g.DrawString(pcts[c] + "%⚡", font, Brushes.Black, pad + c * 2 * (cell + pad) + cell + pad, pad);
                    }
                    for (int r = 0; r < styles.Length; r++)
                        g.DrawString(names[r], font, Brushes.Black, pad, pad + (r + 1) * (cell + pad) - 14);
                }
            }
            board.Save(Path.Combine(outDir, "icon_preview.png"), ImageFormat.Png);
        }
        Console.WriteLine("saved icon_preview.png");
    }
}
