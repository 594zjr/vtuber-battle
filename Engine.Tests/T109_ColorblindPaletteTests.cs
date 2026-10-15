using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

public sealed class T109_ColorblindPaletteTests
{
    // 这是选色前固定的工程回归门槛，不是色觉障碍人群的辨识保证。
    // 在 D65 Lab 中检查模拟图像的 ΔE00 > 10；实际界面仍需同时显示符号和名称。
    const double MinimumDifference = 10;
    const double MinimumContrast = 4.5;
    readonly ITestOutputHelper output;

    public T109_ColorblindPaletteTests(ITestOutputHelper output) { this.output = output; }

    // Machado 2010 学位论文 Appendix A / Table A.1，severity = 1.0，保留原表三位小数。
    // https://www.inf.ufrgs.br/~oliveira/students_dissertations/Masters/Gustavo_Machado_Masters_thesis_UFRGS_2010.pdf
    // 原始模型：Machado, Oliveira & Fernandes (2009), DOI: 10.1109/TVCG.2009.113。
    // tritanomaly 使用光谱偏移近似；论文明确不将它当成准确的 tritanopia 模型。
    static readonly double[][,] Matrices =
    {
        new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } },
        new double[,] { { .152, 1.053, -.205 }, { .115, .786, .099 }, { -.004, -.048, 1.052 } },
        new double[,] { { .367, .861, -.228 }, { .280, .673, .047 }, { -.012, .043, .969 } },
        new double[,] { { 1.256, -.077, -.179 }, { -.078, .931, .148 }, { .005, .691, .304 } }
    };

    static readonly string[] Models = { "normal", "protanomaly", "deuteranomaly", "tritanomaly" };

    [Fact]
    public void EightTypesHaveDistinctSymbolsAndPreserveOriginalColors()
    {
        var types = ReadTypes();
        var originals = new Dictionary<string, string>
        {
            { "song", "#ff7fb6" }, { "game", "#ff8a3d" }, { "talk", "#5fd08a" }, { "art", "#ffd166" },
            { "meme", "#c7a6ff" }, { "asmr", "#5cc6ff" }, { "horror", "#9b7dff" }, { "tech", "#9db4c9" }
        };
        Assert.Equal(originals.Keys.OrderBy(x => x), types.Keys.OrderBy(x => x));
        Assert.Equal(8, types.Values.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, types.Values.Select(x => x.Alternate).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var pair in types)
        {
            Assert.Equal(originals[pair.Key], pair.Value.Original);
            Assert.False(string.IsNullOrWhiteSpace(pair.Value.Symbol));
            Assert.Equal(1, new StringInfo(pair.Value.Symbol).LengthInTextElements);
            Assert.Matches("^#[0-9a-fA-F]{6}$", pair.Value.Alternate);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryPairExceedsPredeterminedDifference(int model)
    {
        var types = ReadTypes().ToArray();
        double minimum = double.PositiveInfinity;
        string closest = "";
        int count = 0;
        for (int i = 0; i < types.Length; i++)
        for (int j = i + 1; j < types.Length; j++)
        {
            var difference = DeltaE00(ToLab(Simulate(types[i].Value.Alternate, model)),
                ToLab(Simulate(types[j].Value.Alternate, model)));
            string pair = types[i].Key + "/" + types[j].Key;
            Assert.True(difference > MinimumDifference,
                $"{Models[model]} {pair}: ΔE00={difference:F6}，要求 >{MinimumDifference}。");
            if (difference < minimum) { minimum = difference; closest = pair; }
            count++;
        }
        Assert.Equal(28, count);
        output.WriteLine($"{Models[model]}: 28 对，最小 ΔE00={minimum:F6} ({closest})");
    }

    [Theory]
    [InlineData("#0c1124")]
    [InlineData("#151c38")]
    [InlineData("#1c2549")]
    public void PaletteContrastsWithThemeTextAndPanelBackgrounds(string background)
    {
        // Theme.uss 的背景、panel、raised；#0c1124 也用于 type-chip 的深色文字。
        // WCAG 2.2 1.4.3 的 4.5:1 作为辅助校准，模拟后的检查是额外工程约束。
        // https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html
        double minimum = double.PositiveInfinity;
        string closest = "";
        foreach (var pair in ReadTypes())
        for (int model = 0; model < Matrices.Length; model++)
        {
            var contrast = Contrast(Simulate(pair.Value.Alternate, model), Simulate(background, model));
            Assert.True(contrast >= MinimumContrast,
                $"{Models[model]} {pair.Key}/{background}: 对比度={contrast:F6}，要求 ≥{MinimumContrast}。");
            if (contrast < minimum) { minimum = contrast; closest = pair.Key + "/" + Models[model]; }
        }
        output.WriteLine($"{background}: 最小对比度={minimum:F6} ({closest})");
    }

    [Fact]
    public void ConversionHandlesSrgbTransferD65WhiteAndClippedGamut()
    {
        Assert.Equal(0, Linear(0), 12);
        Assert.Equal(1, Linear(1), 12);
        Assert.Equal(.0031308049535603713, Linear(.04045), 12);
        Assert.Equal(.21586050011389926, Linear(128.0 / 255), 12);
        var white = ToLab(new[] { 1.0, 1.0, 1.0 });
        Assert.InRange(white[0], 99.9999, 100.0001);
        Assert.InRange(Math.Abs(white[1]), 0, .0001);
        Assert.InRange(Math.Abs(white[2]), 0, .0001);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, ToLab(new[] { 0.0, 0.0, 0.0 }));
        Assert.Equal(21, Contrast(new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 1.0 }), 12);
        Assert.Equal(1, Contrast(new[] { .2, .3, .4 }, new[] { .2, .3, .4 }), 12);
        var blue = Simulate("#0000ff", 1);
        Assert.Equal(new[] { 0.0, .099, 1.0 }, blue);
        var red = Simulate("#ff0000", 3);
        Assert.Equal(new[] { 1.0, 0.0, .005 }, red);
    }

    // Sharma / Wu / Dalal (2005) 的完整 34 对公开参考数据，校准零色度、色相环绕和蓝区旋转项。
    // https://hajim.rochester.edu/ece/sites/gsharma/ciede2000/dataNprograms/ciede2000testdata.txt
    public static IEnumerable<object[]> ReferenceDifferences()
    {
        double[,] rows =
        {
            { 50, 2.6772, -79.7751, 50, 0, -82.7485, 2.0425 },
            { 50, 3.1571, -77.2803, 50, 0, -82.7485, 2.8615 },
            { 50, 2.8361, -74.0200, 50, 0, -82.7485, 3.4412 },
            { 50, -1.3802, -84.2814, 50, 0, -82.7485, 1.0000 },
            { 50, -1.1848, -84.8006, 50, 0, -82.7485, 1.0000 },
            { 50, -.9009, -85.5211, 50, 0, -82.7485, 1.0000 },
            { 50, 0, 0, 50, -1, 2, 2.3669 },
            { 50, -1, 2, 50, 0, 0, 2.3669 },
            { 50, 2.4900, -.0010, 50, -2.4900, .0009, 7.1792 },
            { 50, 2.4900, -.0010, 50, -2.4900, .0010, 7.1792 },
            { 50, 2.4900, -.0010, 50, -2.4900, .0011, 7.2195 },
            { 50, 2.4900, -.0010, 50, -2.4900, .0012, 7.2195 },
            { 50, -.0010, 2.4900, 50, .0009, -2.4900, 4.8045 },
            { 50, -.0010, 2.4900, 50, .0010, -2.4900, 4.8045 },
            { 50, -.0010, 2.4900, 50, .0011, -2.4900, 4.7461 },
            { 50, 2.5, 0, 50, 0, -2.5, 4.3065 },
            { 50, 2.5, 0, 73, 25, -18, 27.1492 },
            { 50, 2.5, 0, 61, -5, 29, 22.8977 },
            { 50, 2.5, 0, 56, -27, -3, 31.9030 },
            { 50, 2.5, 0, 58, 24, 15, 19.4535 },
            { 50, 2.5, 0, 50, 3.1736, .5854, 1.0000 },
            { 50, 2.5, 0, 50, 3.2972, 0, 1.0000 },
            { 50, 2.5, 0, 50, 1.8634, .5757, 1.0000 },
            { 50, 2.5, 0, 50, 3.2592, .3350, 1.0000 },
            { 60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644 },
            { 63.0109, -31.0961, -5.8663, 62.8187, -29.7946, -4.0864, 1.2630 },
            { 61.2901, 3.7196, -5.3901, 61.4292, 2.2480, -4.9620, 1.8731 },
            { 35.0831, -44.1164, 3.7933, 35.0232, -40.0716, 1.5901, 1.8645 },
            { 22.7233, 20.0904, -46.6940, 23.0331, 14.9730, -42.5619, 2.0373 },
            { 36.4612, 47.8580, 18.3852, 36.2715, 50.5065, 21.2231, 1.4146 },
            { 90.8027, -2.0831, 1.4410, 91.1528, -1.6435, .0447, 1.4441 },
            { 90.9257, -.5406, -.9208, 88.6381, -.8985, -.7239, 1.5381 },
            { 6.7747, -.2908, -2.4247, 5.8714, -.0985, -2.2286, .6377 },
            { 2.0776, .0795, -1.1350, .9033, -.0636, -.5514, .9082 }
        };
        for (int row = 0; row < rows.GetLength(0); row++)
            yield return new object[]
            {
                new[] { rows[row, 0], rows[row, 1], rows[row, 2] },
                new[] { rows[row, 3], rows[row, 4], rows[row, 5] }, rows[row, 6]
            };
    }

    [Theory]
    [MemberData(nameof(ReferenceDifferences))]
    public void DeltaE00MatchesPublishedReferenceInBothDirections(double[] first, double[] second, double expected)
    {
        Assert.InRange(Math.Abs(DeltaE00(first, second) - expected), 0, .00005);
        Assert.InRange(Math.Abs(DeltaE00(second, first) - expected), 0, .00005);
        Assert.Equal(0, DeltaE00(first, first), 12);
    }

    static Dictionary<string, (string Original, string Symbol, string Alternate)> ReadTypes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("types").EnumerateObject().ToDictionary(x => x.Name,
            x => (x.Value.GetProperty("color").GetString(), x.Value.GetProperty("symbol").GetString(),
                x.Value.GetProperty("colorAlt").GetString()), StringComparer.Ordinal);
    }

    static double Linear(double value)
    {
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }

    static double[] Simulate(string hex, int model)
    {
        var rgb = new double[3];
        for (int i = 0; i < 3; i++) rgb[i] = Linear(int.Parse(hex.Substring(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0);
        var simulated = new double[3];
        for (int row = 0; row < 3; row++)
        {
            double value = 0;
            for (int col = 0; col < 3; col++) value += Matrices[model][row, col] * rgb[col];
            simulated[row] = Math.Max(0, Math.Min(1, value));
        }
        return simulated;
    }

    static double Contrast(double[] first, double[] second)
    {
        double a = .2126 * first[0] + .7152 * first[1] + .0722 * first[2];
        double b = .2126 * second[0] + .7152 * second[1] + .0722 * second[2];
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    static double[] ToLab(double[] rgb)
    {
        double x = LabCurve((.4124564 * rgb[0] + .3575761 * rgb[1] + .1804375 * rgb[2]) / .95047);
        double y = LabCurve(.2126729 * rgb[0] + .7151522 * rgb[1] + .0721750 * rgb[2]);
        double z = LabCurve((.0193339 * rgb[0] + .1191920 * rgb[1] + .9503041 * rgb[2]) / 1.08883);
        return new[] { 116 * y - 16, 500 * (x - y), 200 * (y - z) };
    }

    static double LabCurve(double value)
    {
        const double delta = 6.0 / 29;
        return value > delta * delta * delta ? Math.Pow(value, 1.0 / 3) : value / (3 * delta * delta) + 4.0 / 29;
    }

    static double Cos(double degrees) { return Math.Cos(degrees * Math.PI / 180); }
    static double Sin(double degrees) { return Math.Sin(degrees * Math.PI / 180); }
    static double Hue(double a, double b)
    {
        if (a == 0 && b == 0) return 0;
        var angle = Math.Atan2(b, a) * 180 / Math.PI;
        return angle < 0 ? angle + 360 : angle;
    }

    static double DeltaE00(double[] first, double[] second)
    {
        double c1 = Math.Sqrt(first[1] * first[1] + first[2] * first[2]);
        double c2 = Math.Sqrt(second[1] * second[1] + second[2] * second[2]);
        double cMean7 = Math.Pow((c1 + c2) / 2, 7);
        double g = .5 * (1 - Math.Sqrt(cMean7 / (cMean7 + Math.Pow(25, 7))));
        double a1 = (1 + g) * first[1], a2 = (1 + g) * second[1];
        double cp1 = Math.Sqrt(a1 * a1 + first[2] * first[2]);
        double cp2 = Math.Sqrt(a2 * a2 + second[2] * second[2]);
        double h1 = Hue(a1, first[2]), h2 = Hue(a2, second[2]);
        double dl = second[0] - first[0], dc = cp2 - cp1;
        double hueDelta = h2 - h1;
        if (cp1 * cp2 == 0) hueDelta = 0;
        else if (hueDelta > 180) hueDelta -= 360;
        else if (hueDelta < -180) hueDelta += 360;
        double dh = 2 * Math.Sqrt(cp1 * cp2) * Sin(hueDelta / 2);
        double lm = (first[0] + second[0]) / 2, cm = (cp1 + cp2) / 2;
        double hm = (h1 + h2) / 2;
        if (cp1 * cp2 == 0) hm = h1 + h2;
        else if (Math.Abs(h1 - h2) > 180) hm += h1 + h2 < 360 ? 180 : -180;
        double t = 1 - .17 * Cos(hm - 30) + .24 * Cos(2 * hm) + .32 * Cos(3 * hm + 6) - .20 * Cos(4 * hm - 63);
        double lightness = (lm - 50) * (lm - 50);
        double sl = 1 + .015 * lightness / Math.Sqrt(20 + lightness);
        double sc = 1 + .045 * cm, sh = 1 + .015 * cm * t;
        double cm7 = Math.Pow(cm, 7);
        double rt = -2 * Math.Sqrt(cm7 / (cm7 + Math.Pow(25, 7))) * Sin(60 * Math.Exp(-Math.Pow((hm - 275) / 25, 2)));
        double l = dl / sl, c = dc / sc, h = dh / sh;
        return Math.Sqrt(l * l + c * c + h * h + rt * c * h);
    }
}
