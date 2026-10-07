using System.Globalization;
using System.Reflection;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.token;
using Xunit;

namespace AquariusREPL.interpret;

// Console.SetOut affects the entire process, including the other interpreter tests.
[CollectionDefinition("Starship console output", DisableParallelization = true)]
public class StarshipConsoleCollection { }

[Collection("Starship console output")]
public class StarshipExpeditionTest {
    private static readonly string ExampleDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "examples/starship_expedition"));

    [Fact]
    public void ExpeditionProducesExpectedResultsAndChineseOutput() {
        TextWriter originalOutput = Console.Out;
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        using StringWriter output = new StringWriter(CultureInfo.InvariantCulture);
        IObject evaluated;
        try {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Console.SetOut(output);
            evaluated = Interpreter.Interpret(Path.Combine(ExampleDirectory, "main.aqua"));
        } finally {
            Console.SetOut(originalOutput);
            CultureInfo.CurrentCulture = originalCulture;
        }

        IObject[] results = Assert.IsType<ArrayObj>(evaluated).Elements;
        Assert.Collection(results,
            value => AssertIntegers(value, 2, 3, 5, 7, 11, 13, 17, 19, 23, 29),
            value => AssertIntegers(value, 0, 1, 1, 2, 3, 5, 8, 13),
            value => AssertIntegers(value, 0, 1, 1, 4, 9, 25, 64, 169),
            value => AssertIntegers(value, 18, 15, 10, 3),
            value => Assert.Equal(3, Assert.IsType<IntegerObj>(value).Value),
            value => Assert.Equal(8, Assert.IsType<IntegerObj>(value).Value),
            value => Assert.Equal(2.0f, Assert.IsType<FloatObj>(value).Value),
            value => Assert.Equal(1.5d, Assert.IsType<DoubleObj>(value).Value),
            value => Assert.Equal(1, Assert.IsType<IntegerObj>(value).Value),
            value => Assert.Equal(3, Assert.IsType<IntegerObj>(value).Value),
            value => Assert.True(Assert.IsType<BooleanObj>(value).Value));

        string expected = File.ReadAllText(Path.Combine(ExampleDirectory, "expected-output.txt"));
        Assert.Equal(expected.Replace("\r\n", "\n"), output.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void ExpeditionParsesAndCoversEveryLanguageTokenWithChineseIdentifiers() {
        HashSet<string> coveredTypes = new HashSet<string>();
        foreach (string file in Directory.GetFiles(ExampleDirectory, "*.aqua")) {
            string source = File.ReadAllText(file);
            Lexer lexer = Lexer.NewInstance(source);
            Token token;
            do {
                token = lexer.NextToken();
                Assert.NotEqual(TokenType.ILLEGAL, token.Type);
                coveredTypes.Add(token.Type);
                if (token.Type == TokenType.IDENT) {
                    Assert.Matches(@"^[\u3400-\u4DBF\u4E00-\u9FFF_0-9]+$", token.Literal);
                }
            } while (token.Type != TokenType.EOF);
            Assert.Empty(lexer.Errors);

            Parser parser = Parser.NewInstance(Lexer.NewInstance(source));
            parser.ParseAST();
            Assert.Empty(parser.Errors);
        }

        // New syntax must be represented in the comprehensive example as it is added.
        foreach (FieldInfo field in typeof(TokenType).GetFields(BindingFlags.Public | BindingFlags.Static)) {
            string type = (string)field.GetRawConstantValue()!;
            if (type != TokenType.ILLEGAL) {
                Assert.Contains(type, coveredTypes);
            }
        }
    }

    private static void AssertIntegers(IObject value, params int[] expected) {
        IObject[] elements = Assert.IsType<ArrayObj>(value).Elements;
        Assert.Equal(expected, elements.Select(element => Assert.IsType<IntegerObj>(element).Value).ToArray());
    }
}
