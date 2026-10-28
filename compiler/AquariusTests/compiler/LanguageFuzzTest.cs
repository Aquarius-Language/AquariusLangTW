using System.Text;

namespace AquariusTests.Wasm;

/// <summary>Monkey tests: deterministic random programs over integers, doubles, typed arrays, struct fields, compound
/// assignments, ++ on variables, elements and fields, conditionals, loops, breaks and continues. Each program runs in a typed
/// version (annotations everywhere, so the native tier compiles it) and an untyped one (clones, regions and the generic
/// code). Both emitters must agree on every program, and the typed and untyped versions must compute the same values.</summary>
public class LanguageFuzzTest
{
    // AQUARIUS_FUZZ=<first seed>,<count> runs a longer search with other seeds.
    private static readonly int[] Seeds = Environment.GetEnvironmentVariable("AQUARIUS_FUZZ")?.Split(',').Select(int.Parse).ToArray() ?? [0, 240];
    private static readonly int Programs = Seeds[1];

    /// <summary>Generates one program. The typed and untyped versions come from the same seed, so they are the same
    /// program with and without annotations.</summary>
    private sealed class Generator
    {
        private readonly Random random;
        private readonly bool typed;
        private readonly List<string> ints = ["x", "k"];
        private int loops, depth;
        private readonly StringBuilder body = new();

        private Generator(int seed, bool typed) { random = new Random(seed); this.typed = typed; }

        public static string Program(int seed, bool typed) => new Generator(seed, typed).Build();

        private string T(string type) => typed ? ": " + type : "";
        private bool Chance(int percent) => random.Next(100) < percent;
        private T Pick<T>(params T[] items) => items[random.Next(items.Length)];

        private string Build()
        {
            int statements = random.Next(3, 9);
            for (int i = 0; i < statements; i++) Statement("    ");
            var a = string.Join(", ", Enumerable.Range(0, random.Next(1, 6)).Select(_ => random.Next(-9, 30)));
            var b = string.Join(", ", Enumerable.Range(0, random.Next(1, 5)).Select(_ => Double()));
            var xs = string.Join(", ", Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(0, 9)));
            return $$"""
                {{(typed ? "struct S { n: int; d: double; xs: int[] }" : "struct S { n; d; xs }")}}
                var f = function(a{{T("int[]")}}, b{{T("double[]")}}, s{{T("S")}}, k{{T("int")}}){{T("int")}} {
                    var x{{T("int")}} = k;
                    var y{{T("double")}} = 0.5d;
                {{body}}    return x + a[0] + s.n + int(y);
                };
                var a = [{{a}}];
                var b = [{{b}}];
                var s = S({{random.Next(-5, 5)}}, {{Double()}}, [{{xs}}]);
                var r = f(a, b, s, {{random.Next(-3, 12)}});
                [r, a, b, s.n, s.d, s.xs];
                """;
        }

        private string Double() => (random.Next(-40, 40) / 4.0).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "d";

        // An index into an array: in range, except rarely (then both versions report the same error or value).
        private string Index(string array) => Chance(1) ? Int() : $"(({Int()}) % len({array}) + len({array})) % len({array})";
        private string NonZero() => Chance(5) ? Int() : Pick("1", "2", "3", "7", "-4");

        private string Int()
        {
            if (++depth > 3 || Chance(40)) {
                depth--;
                return random.Next(8) switch {
                    0 => random.Next(-20, 100).ToString(),
                    1 or 2 => Pick(ints.ToArray()),
                    3 => $"a[{Index("a")}]",
                    4 => "s.n",
                    5 => "len(a)",
                    6 => $"s.xs[{Index("s.xs")}]",
                    _ => random.Next(0, 10).ToString(),
                };
            }
            string result = random.Next(8) switch {
                0 => $"({Int()} + {Int()})",
                1 => $"({Int()} - {Int()})",
                2 => $"({Int()} * {Int()})",
                3 => $"({Int()} / {NonZero()})",
                4 => $"({Int()} % {NonZero()})",
                5 => $"int({DoubleExpression()})",
                6 => $"-{Int()}",
                _ => $"({Int()} + 1)",
            };
            depth--;
            return result;
        }

        private string DoubleExpression()
        {
            if (++depth > 3 || Chance(40)) {
                depth--;
                return random.Next(4) switch { 0 => Double(), 1 => "y", 2 => $"b[{Index("b")}]", _ => "s.d" };
            }
            string result = random.Next(5) switch {
                0 => $"({DoubleExpression()} + {DoubleExpression()})",
                1 => $"({DoubleExpression()} * {DoubleExpression()})",
                2 => $"({DoubleExpression()} - {Int()})",
                3 => $"({DoubleExpression()} / {DoubleExpression()})",
                _ => $"double({Int()})",
            };
            depth--;
            return result;
        }

        private string Condition() => random.Next(5) switch {
            0 => $"{Int()} < {Int()}",
            1 => $"{DoubleExpression()} >= {DoubleExpression()}",
            2 => $"{Int()} == {Int()}",
            3 => $"!({Int()} > {Int()})",
            _ => $"({Int()} <= {Int()}) && ({Int()} != {Int()})",
        };

        private string Compound() => Pick("+=", "-=", "*=");

        private void Statement(string indent)
        {
            int kind = random.Next(loops < 2 && indent.Length < 16 ? 22 : 19);
            string line = kind switch {
                0 => $"x {Compound()} {Int()};",
                1 => $"x {Pick("/=", "%=")} {NonZero()};",
                2 => $"y {Compound()} {DoubleExpression()};",
                3 => $"y += {Int()};",
                4 => $"a[{Index("a")}] {Compound()} {Int()};",
                5 => $"a[{Index("a")}]{Pick("++;", " %= 5;", " /= 2;")}",
                6 => $"++a[{Index("a")}];",
                7 => $"a[{Index("a")}] = {Int()};",
                8 => $"b[{Index("b")}] {Compound()} {DoubleExpression()};",
                9 => $"b[{Index("b")}]++;",
                10 => $"s.n {Compound()} {Int()};",
                11 => Pick("s.n++;", "++s.n;", "s.d++;"),
                12 => $"s.d {Compound()} {DoubleExpression()};",
                13 => $"s.xs[{Index("s.xs")}] {Compound()} {Int()};",
                14 => $"s.xs[{Index("s.xs")}]++;",
                15 => $"x = a[{Index("a")}]++ + ++s.n;",
                16 => $"x = {Int()};",
                17 => $"y = {DoubleExpression()};",
                18 => $"x += {Pick("x++", "++x", "k++")};",
                _ => "",
            };
            if (line.Length > 0) { body.Append(indent).Append(line).Append('\n'); return; }
            if (kind == 19) {
                body.Append(indent).Append($"if ({Condition()}) {{\n");
                for (int i = random.Next(1, 4); i > 0; i--) Statement(indent + "    ");
                body.Append(indent).Append("} else {\n");
                for (int i = random.Next(0, 3); i > 0; i--) Statement(indent + "    ");
                body.Append(indent).Append("}\n");
                return;
            }
            string counter = "i" + loops++;
            body.Append(indent).Append($"for (var {counter}{T("int")} = 0; {counter} < {random.Next(1, 6)}; {counter}++) {{\n");
            ints.Add(counter);
            for (int i = random.Next(1, 4); i > 0; i--) Statement(indent + "    ");
            if (Chance(30)) body.Append(indent).Append($"    if ({Condition()}) {{ {Pick("break", "continue")}; }}\n");
            for (int i = random.Next(0, 2); i > 0; i--) Statement(indent + "    ");
            ints.Remove(counter);
            body.Append(indent).Append("}\n");
        }
    }

    [RuntimeFact]
    public void RandomProgramsAgreeAcrossTiersAndAnnotations()
    {
        var typed = Enumerable.Range(Seeds[0], Programs).Select(seed => Generator.Program(seed, true)).ToList();
        var untyped = Enumerable.Range(Seeds[0], Programs).Select(seed => Generator.Program(seed, false)).ToList();
        var typedValues = EmitterTiers.AssertSame(typed);
        var untypedValues = EmitterTiers.AssertSame(untyped);
        // The generator writes only valid programs: any error is a runtime error.
        var invalid = Enumerable.Range(0, Programs).Where(i => typedValues[i].Type == "error" && !typedValues[i].Text.StartsWith("ERROR: ")
            || untypedValues[i].Type == "error" && !untypedValues[i].Text.StartsWith("ERROR: ")).ToList();
        Assert.True(invalid.Count == 0, "Programs failed to compile:\n" + string.Join('\n', invalid.Take(3).Select(i => $"{typed[i]}\n  {typedValues[i]}\n  {untypedValues[i]}")));
        // Out-of-range reads differ by design (an error in a typed array, null in an untyped one), so only programs that
        // finish in both versions are compared; most programs must finish.
        var finished = Enumerable.Range(0, Programs).Where(i => typedValues[i].Type != "error" && untypedValues[i].Type != "error").ToList();
        Assert.True(finished.Count > Programs * 3 / 4, $"Only {finished.Count} of {Programs} programs finished:\n" +
            string.Join('\n', Enumerable.Range(0, Programs).Where(i => !finished.Contains(i)).Take(3).Select(i => $"{typed[i]}\n  {typedValues[i]}")));
        var mismatches = finished.Where(i => typedValues[i] != untypedValues[i])
            .Select(i => $"{typed[i]}\n  typed:   {typedValues[i]}\n  untyped: {untypedValues[i]}").ToList();
        Assert.True(mismatches.Count == 0, "Typed and untyped versions differ:\n" + string.Join('\n', mismatches.Take(5)));
    }
}
