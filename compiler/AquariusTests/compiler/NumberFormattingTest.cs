using System.Globalization;

namespace AquariusTests.Wasm;

/// <summary>Numbers print the same through the host's 印出 (runtime/host/format.c) and the guest's 字串.轉文字 / String.From
/// (runtime/guest/texts.h): both use runtime/guest/decimal.h. The text is the shortest that reads back to the same value,
/// in the host's style (1E+17, 1E-05, -0).</summary>
public class NumberFormattingTest
{
    private static string DoubleLiteral(double d) => d.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e") + "d";
    private static string FloatLiteral(float f) => f.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e") + "f";

    [RuntimeFact]
    public void PrintAndStringFromAgreeAndReadBack()
    {
        var random = new Random(20261102);
        var doubles = new List<double> { 1e17, 1e16, 123456789012345678, 1e-5, 1e-4, 0.0001234, 1e21, 1e300, double.Epsilon, double.MaxValue, 2.2250738585072014e-308,
            0.1, 0.3, 1.0 / 3, 9007199254740993, 5e-324, 1.5, 100, 12345.678 };
        var floats = new List<float> { 1e9f, 1e8f, 123456789f, 1e-5f, float.Epsilon, float.MaxValue, 0.1f, 16777217f, 3.4028235e38f, 1.17549435e-38f, 0.3f };
        for (int i = 0; i < 300; i++) {
            double d = BitConverter.Int64BitsToDouble(random.NextInt64() & 0x7fffffffffffffff);
            if (double.IsFinite(d)) doubles.Add(random.Next(2) == 0 ? d : -d);
            doubles.Add(Math.Round(random.NextDouble() * Math.Pow(10, random.Next(-8, 22)), random.Next(0, 6)));
            float f = BitConverter.Int32BitsToSingle(random.Next() & 0x7fffffff);
            if (float.IsFinite(f)) floats.Add(f);
        }
        var literals = doubles.Select(DoubleLiteral).Concat(floats.Select(FloatLiteral)).ToList();
        string program = string.Concat(literals.Select(l => $"變數 v = {l}; 印出(v); 印出(字串.轉文字(v));\n"));
        var lines = WasmTestHost.Output(program).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(literals.Count * 2, lines.Length);
        for (int i = 0; i < literals.Count; i++) {
            string printed = lines[2 * i], text = lines[2 * i + 1];
            Assert.True(printed == text, $"{literals[i]}: 印出 {printed}, String.From {text}");
            // The text reads back to the same value, and never has more digits than .NET's shortest round trip.
            if (i < doubles.Count) {
                Assert.Equal(doubles[i], double.Parse(printed, CultureInfo.InvariantCulture));
                Assert.True(Digits(printed) <= Digits(doubles[i].ToString("R", CultureInfo.InvariantCulture)), $"{printed} vs {doubles[i]:R}");
            } else {
                float f = floats[i - doubles.Count];
                Assert.Equal(f, float.Parse(printed, CultureInfo.InvariantCulture));
                Assert.True(Digits(printed) <= Digits(f.ToString("R", CultureInfo.InvariantCulture)), $"{printed} vs {f:R}");
            }
        }
        static int Digits(string text) => text.Split('E')[0].Trim('-').Replace(".", "").Trim('0').Length;
    }

    [RuntimeFact]
    public void SpecialValuesAndThresholds() => LanguageCases.Expect(
        ("[String.From(-(0.0d)),String.From(-(0.0f)),String.From(1000000000.0f),String.From(100000000.0f),String.From(1e300),String.From(5e-324)," +
         "String.From(0.0001),String.From(0.00001),String.From(Math.Infinity),String.From(-Math.Infinity),String.From(Math.NaN),String.From(16777217.0f)," +
         "String.From(100000000000000000.0d),String.From(10000000000000000.0d)];",
         "array", "[-0, -0, 1E+09, 100000000, 1E+300, 5E-324, 0.0001, 1E-05, Infinity, -Infinity, NaN, 16777216, 1E+17, 10000000000000000]"),
        ("String.Join([1.5, -(0.0d), 0.0000001, 2.5f], \", \");", "string", "1.5, -0, 1E-07, 2.5"));
}
