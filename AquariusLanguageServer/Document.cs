using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.token;
using AquariusLang.runtime;

namespace AquariusLanguageServer;

internal sealed class Scope(int start, int end, Scope? parent)
{
    public int Start { get; } = start;
    public int End { get; } = end;
    public Scope? Parent { get; } = parent;
    public List<Declaration> Declarations { get; } = [];
}

internal sealed record Declaration(Token Token, Scope Scope, int Kind, string Detail, int End, IExpression? Value);
internal sealed record Diagnostic(TextRange range, string message, int severity = 1, string source = "aquarius");

internal sealed class Document
{
    public string Uri { get; }
    public int Version { get; }
    public SourceText Source { get; }
    public List<Diagnostic> Diagnostics { get; } = [];
    private readonly List<Token> tokens = [];
    private readonly List<Scope> scopes = [];
    private readonly List<Declaration> declarations = [];
    private readonly Dictionary<int, int> blockEnds = [];
    private int walkDepth;
    private readonly Func<string, Document?>? resolveDocument;
    private readonly Dictionary<int, (IExpression Receiver, Scope Scope)> memberReceivers = [];
    private sealed record ModuleInfo(string? Library = null, Document? Script = null);

    public Document(string uri, int version, string text, Func<string, Document?>? resolveDocument = null)
    {
        Uri = uri;
        Version = version;
        Source = new SourceText(text);
        this.resolveDocument = resolveDocument;
        var lexer = Lexer.NewInstance(text);
        var braces = new Stack<Token>();
        Token token;
        do
        {
            token = lexer.NextToken();
            tokens.Add(token);
            if (token.Type == TokenType.ILLEGAL) AddError("Invalid token or unterminated string.", token);
            if (token.Type == TokenType.LBRACE) braces.Push(token);
            if (token.Type == TokenType.RBRACE && braces.TryPop(out var opening))
                blockEnds[opening.Start] = token.Start + token.Length;
        } while (token.Type != TokenType.EOF);
        foreach (var error in lexer.Errors)
            Diagnostics.Add(new Diagnostic(Source.Range(error.Start, error.Length), error.Message));

        var root = new Scope(0, text.Length, null);
        scopes.Add(root);
        var parser = Parser.NewInstance(Lexer.NewInstance(text));
        try
        {
            var ast = parser.ParseAST();
            foreach (var error in parser.Diagnostics) AddError(error.Message, error.Token);
            Walk(ast, root);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException or FormatException or OverflowException)
        {
            foreach (var diagnostic in parser.Diagnostics) AddError(diagnostic.Message, diagnostic.Token);
            if (Diagnostics.Count == 0)
                Diagnostics.Add(new Diagnostic(Source.Range(0, 0), $"Unable to parse this incomplete expression: {error.Message}"));
        }
    }

    private void AddError(string message, Token token)
    {
        var diagnostic = new Diagnostic(Source.Range(token.Start, token.Length), message);
        if (!Diagnostics.Contains(diagnostic) && Diagnostics.Count < 100) Diagnostics.Add(diagnostic);
    }

    private int End(BlockStatement? body) => body == null ? Source.Text.Length
        : blockEnds.GetValueOrDefault(body.Token.Start, Source.Text.Length);

    private Scope Child(int start, int end, Scope parent)
    {
        var scope = new Scope(start, end, parent);
        scopes.Add(scope);
        return scope;
    }

    private void Declare(Identifier? identifier, Scope scope, int kind, string detail, int? end = null, IExpression? value = null)
    {
        if (identifier == null || identifier.Token.Type != TokenType.IDENT) return;
        var declaration = new Declaration(identifier.Token, scope, kind, detail, end ?? identifier.Token.Start + identifier.Token.Length, value);
        declarations.Add(declaration);
        scope.Declarations.Add(declaration);
    }

    // Use the language frontend's AST for declarations; hash braces and strings never become scopes.
    // Conditional blocks share their environment, while functions and loops introduce one.
    private void Walk(INode? node, Scope scope)
    {
        if (walkDepth >= 256) throw new InvalidOperationException("Syntax tree nesting exceeds the limit of 256.");
        walkDepth++;
        try { WalkCore(node, scope); }
        finally { walkDepth--; }
    }

    private void WalkCore(INode? node, Scope scope)
    {
        switch (node)
        {
            case AbstractSyntaxTree ast:
                foreach (var statement in ast.Statements) Walk(statement, scope);
                break;
            case BlockStatement block:
                foreach (var statement in block.Statements ?? []) Walk(statement, scope);
                break;
            case LetStatement let:
                var function = let.Value as FunctionLiteral;
                string names = let.Alias == null ? let.Name?.Value ?? "" : $"{let.Name?.Value} / {let.Alias.Value}";
                string detail = function == null ? $"變數 {names}" : $"{names}({string.Join(", ", (function.Parameters ?? []).Select(p => p.Value))})";
                Declare(let.Name, scope, function == null ? 13 : 12, detail, function == null ? null : End(function.Body), let.Value);
                Declare(let.Alias, scope, 12, detail, function == null ? null : End(function.Body), let.Value);
                Walk(let.Value, scope);
                break;
            case FunctionLiteral fn:
                var fnScope = Child(fn.Token.Start, End(fn.Body), scope);
                foreach (var parameter in fn.Parameters ?? []) Declare(parameter, fnScope, 13, $"參數 {parameter.Value}");
                Walk(fn.Body, fnScope);
                break;
            case ForLoopLiteral loop:
                var loopScope = Child(loop.Token.Start, End(loop.Body), scope);
                Walk(loop.DeclareStatement, loopScope);
                Walk(loop.ConditionalExpression, loopScope);
                Walk(loop.ValueChangeStatement, loopScope);
                Walk(loop.Body, loopScope);
                break;
            case ExpressionStatement expression: Walk(expression.Expression, scope); break;
            case ReturnStatement statement: Walk(statement.ReturnValue, scope); break;
            case PrefixExpression prefix: Walk(prefix.Right, scope); break;
            case IncrementExpression increment: Walk(increment.Operand, scope); break;
            case InfixExpression infix:
                if (infix.Operator == "." && infix.Left != null) memberReceivers[infix.Token.Start] = (infix.Left, scope);
                Walk(infix.Left, scope); Walk(infix.Right, scope); break;
            case CallExpression call:
                Walk(call.Function, scope);
                foreach (var arg in call.Arguments ?? []) Walk(arg, scope);
                break;
            case IfExpression conditional:
                Walk(conditional.Condition, scope);
                Walk(conditional.Consequence, scope);
                foreach (var condition in conditional.AlternativeConditions ?? []) Walk(condition, scope);
                foreach (var alternative in conditional.Alternatives ?? []) Walk(alternative, scope);
                Walk(conditional.LastResort, scope);
                break;
            case ArrayLiteral array:
                foreach (var element in array.Elements ?? []) Walk(element, scope);
                break;
            case IndexExpression index: Walk(index.Left, scope); Walk(index.Index, scope); break;
            case HashLiteral hash:
                foreach (var pair in hash.Pairs ?? []) { Walk(pair.Key, scope); Walk(pair.Value, scope); }
                break;
        }
    }

    private Scope ScopeAt(int offset) => scopes.Last(s => s.Start <= offset && (offset < s.End || s.Parent == null));

    private IEnumerable<Declaration> Visible(int offset)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (Scope? scope = ScopeAt(offset); scope != null; scope = scope.Parent)
            foreach (var declaration in scope.Declarations.AsEnumerable().Reverse())
                if (declaration.Token.Start <= offset && names.Add(declaration.Token.Literal)) yield return declaration;
    }

    private Token? At(int offset) => tokens.Where(t => t.Start <= offset && offset < t.Start + t.Length)
        .Select(t => (Token?)t).FirstOrDefault();

    private bool IsMember(Token token)
    {
        int index = tokens.FindIndex(t => t.Start == token.Start);
        return index > 0 && tokens[index - 1].Type == TokenType.DOT;
    }

    private Declaration? Lookup(string name, Scope scope, int offset)
    {
        for (Scope? current = scope; current != null; current = current.Parent)
        {
            var found = current.Declarations.LastOrDefault(d => d.Token.Literal == name && d.Token.Start <= offset);
            if (found != null) return found;
        }
        return null;
    }

    private string? ImportPath(IExpression? expression, Scope scope, int offset, int depth = 0)
    {
        if (depth > 32) return null;
        if (expression is StringLiteral text) return text.Value;
        if (expression is InfixExpression { Operator: "+" } sum)
        {
            string? left = ImportPath(sum.Left, scope, offset, depth + 1), right = ImportPath(sum.Right, scope, offset, depth + 1);
            return left == null || right == null ? null : left + right;
        }
        if (expression is Identifier identifier)
        {
            var declaration = Lookup(identifier.Value, scope, offset);
            if (declaration != null) return ImportPath(declaration.Value, declaration.Scope, declaration.Token.Start, depth + 1);
            if (identifier.Value is "目前工作目錄" or "currWorkingDir" && System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) && uri.IsFile)
                return Path.GetDirectoryName(uri.LocalPath);
        }
        return null;
    }

    private ModuleInfo? ResolveModule(IExpression? expression, Scope scope, int offset, int depth = 0)
    {
        if (depth > 32) return null;
        if (expression is Identifier identifier)
        {
            var declaration = Lookup(identifier.Value, scope, offset);
            return declaration == null ? null : ResolveModule(declaration.Value, declaration.Scope, declaration.Token.Start, depth + 1);
        }
        if (expression is CallExpression call && call.Function is Identifier importer &&
            importer.Value is "匯入" or "import" && call.Arguments is [var pathExpression] && Lookup(importer.Value, scope, offset) == null)
        {
            string? path = ImportPath(pathExpression, scope, offset);
            if (path == null) return null;
            string library = LibraryCatalog.CanonicalLibrary(path);
            if (LibraryCatalog.Members(library).Any()) return new ModuleInfo(Library: library);
            if (resolveDocument == null || !System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            try
            {
                string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(uri.LocalPath)!, path));
                var imported = resolveDocument(new System.Uri(file).AbsoluteUri);
                return imported == null ? null : new ModuleInfo(Script: imported);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        }
        if (expression is InfixExpression { Operator: "." } member)
        {
            var receiver = ResolveModule(member.Left, scope, offset, depth + 1);
            string? name = member.Right is Identifier id ? id.Value : (member.Right as CallExpression)?.Function is Identifier fn ? fn.Value : null;
            if (receiver?.Library != null && name != null && member.Right is CallExpression)
            {
                string? returns = LibraryCatalog.Find(receiver.Library, name)?.ReturnLibrary;
                return returns == null ? null : new ModuleInfo(Library: returns);
            }
            if (receiver?.Script != null && name != null && member.Right is Identifier)
            {
                var declaration = receiver.Script.Exports().LastOrDefault(d => d.Token.Literal == name);
                return declaration == null ? null : receiver.Script.ResolveModule(declaration.Value, declaration.Scope, declaration.Token.Start, depth + 1);
            }
        }
        return null;
    }

    private IEnumerable<Declaration> Exports() => declarations.Where(d => d.Scope.Parent == null)
        .GroupBy(d => d.Token.Literal).Select(g => g.Last());

    private ModuleInfo? MemberModule(Token token, int offset)
    {
        int index = tokens.FindIndex(t => t.Start == token.Start);
        int dot = token.Type == TokenType.DOT ? index : index - 1;
        if (dot < 1 || tokens[dot].Type != TokenType.DOT) return null;
        if (memberReceivers.TryGetValue(tokens[dot].Start, out var member))
            return ResolveModule(member.Receiver, member.Scope, offset);
        // Incomplete `module.` expressions may not survive parsing; resolve their simple receiver.
        var previous = tokens[dot - 1];
        return previous.Type == TokenType.IDENT ? ResolveModule(new Identifier(previous, previous.Literal), ScopeAt(offset), offset) : null;
    }

    private static IEnumerable<object> MemberCompletion(ModuleInfo? module, string prefix)
    {
        if (module?.Library != null)
            return LibraryCatalog.Members(module.Library).SelectMany(f => new[] { f.TraditionalChineseName, f.EnglishName }
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => (object)new { label = n, kind = 3, detail = f.Detail }));
        return module?.Script?.Exports().Where(d => d.Token.Literal.StartsWith(prefix, StringComparison.Ordinal))
            .Select(d => (object)new { label = d.Token.Literal, kind = d.Kind == 12 ? 3 : 6, detail = d.Detail }) ?? [];
    }

    public object Completion(Position position)
    {
        int offset = Source.Offset(position);
        if (!Source.IsCode(offset)) return Array.Empty<object>();
        var token = tokens.LastOrDefault(t => t.Start < offset && offset <= t.Start + t.Length);
        string prefix = token.Type == TokenType.IDENT || LanguageFacts.Keywords.ContainsKey(token.Literal ?? "")
            ? Source.Text[token.Start..offset] : "";
        if (token.Type == TokenType.DOT || (token.Type == TokenType.IDENT && IsMember(token)))
            return MemberCompletion(MemberModule(token, offset), prefix).ToArray();
        var items = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in LanguageFacts.Keywords)
            items[entry.Key] = new { label = entry.Key, kind = 14, detail = entry.Value };
        foreach (var entry in LanguageFacts.Builtins)
            items[entry.Key] = new { label = entry.Key, kind = entry.Key is "目前工作目錄" or "currWorkingDir" ? 6 : 3, detail = entry.Value };
        foreach (var declaration in Visible(offset))
            items[declaration.Token.Literal] = new { label = declaration.Token.Literal, kind = declaration.Kind == 12 ? 3 : 6, detail = declaration.Detail };
        return items.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(item => item.Value).ToArray();
    }

    public object? Hover(Position position)
    {
        int offset = Source.Offset(position);
        if (At(offset) is not Token token) return null;
        if (token.Type == TokenType.STRING || token.Type == TokenType.ILLEGAL) return null;
        if (IsMember(token))
        {
            var module = MemberModule(token, offset);
            string? memberDetail = module?.Library != null ? LibraryCatalog.Find(module.Library, token.Literal)?.Detail
                : module?.Script?.Exports().LastOrDefault(d => d.Token.Literal == token.Literal)?.Detail;
            return memberDetail == null ? null : new { contents = new { kind = "plaintext", value = memberDetail }, range = Source.Range(token.Start, token.Length) };
        }
        string? detail = Visible(offset).FirstOrDefault(d => d.Token.Literal == token.Literal)?.Detail;
        if (detail == null) LanguageFacts.Keywords.TryGetValue(token.Literal, out detail);
        if (detail == null) LanguageFacts.Builtins.TryGetValue(token.Literal, out detail);
        return detail == null ? null : new { contents = new { kind = "plaintext", value = detail }, range = Source.Range(token.Start, token.Length) };
    }

    public object? Definition(Position position)
    {
        int offset = Source.Offset(position);
        if (At(offset) is not Token token || token.Type != TokenType.IDENT) return null;
        if (IsMember(token))
        {
            var imported = MemberModule(token, offset)?.Script;
            var member = imported?.Exports().LastOrDefault(d => d.Token.Literal == token.Literal);
            return member == null ? null : new { uri = imported!.Uri, range = imported.Source.Range(member.Token.Start, member.Token.Length) };
        }
        var declaration = Visible(offset).FirstOrDefault(d => d.Token.Literal == token.Literal);
        return declaration == null ? null : new { uri = Uri, range = Source.Range(declaration.Token.Start, declaration.Token.Length) };
    }

    public object Symbols() => declarations.Select(d => new
    {
        name = d.Token.Literal, kind = d.Kind,
        location = new { uri = Uri, range = Source.Range(d.Token.Start, d.End - d.Token.Start) },
        containerName = d.Scope.Parent == null ? "" : "local"
    }).ToArray();
}
