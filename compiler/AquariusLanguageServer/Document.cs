using AquariusLang.ast;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.token;

using AquariusLang.Bottle;

namespace AquariusLanguageServer;

internal sealed class Scope(int start, int end, Scope? parent)
{
    public int Start { get; } = start;
    public int End { get; } = end;
    public Scope? Parent { get; } = parent;
    public List<Declaration> Declarations { get; } = [];
}

internal sealed record Declaration(Token Token, Scope Scope, int Kind, string Detail, int End, IExpression? Value, TypeAnnotation? Type = null, StructStatement? Struct = null);
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
    public bool IsManifest => System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);
    private readonly Func<string, Document?>? resolveDocument;
    private readonly Dictionary<int, (IExpression Receiver, Scope Scope)> memberReceivers = [];
    private sealed record ModuleInfo(CatalogModule? Module = null, Document? Script = null);
    private ModuleCatalog? catalog;
    /// <summary>The modules this file can import (its project's libraries, the runtime's native modules).</summary>
    private ModuleCatalog Catalog => catalog ??= ModuleCatalog.For(System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null);

    public Document(string uri, int version, string text, Func<string, Document?>? resolveDocument = null, bool inspectProject = true)
    {
        Uri = uri;
        Version = version;
        Source = new SourceText(text);
        this.resolveDocument = resolveDocument;
        if (IsManifest) { if (inspectProject) RefreshProjectDiagnostics(); return; }
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
            // Annotation mismatches the compiler would reject.
            if (parser.Diagnostics.Count == 0) foreach (var error in TypeChecker.Check(ast).Errors) AddError(error.Message, error.Token);
            Walk(ast, root);
            // Async misuse the compiler accepts: warnings and hints.
            if (parser.Diagnostics.Count == 0)
                foreach (var finding in AsyncChecks.Check(ast, Catalog.AsyncMembers)) AddError(finding.Message, finding.Token, finding.Severity);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException or FormatException or OverflowException)
        {
            foreach (var diagnostic in parser.Diagnostics) AddError(diagnostic.Message, diagnostic.Token);
            if (Diagnostics.Count == 0)
                Diagnostics.Add(new Diagnostic(Source.Range(0, 0), $"Unable to parse this incomplete expression: {error.Message}"));
        }
        if (inspectProject) RefreshProjectDiagnostics();
    }

    public void RefreshProjectDiagnostics() {
        Diagnostics.RemoveAll(d => d.source == "aquarius-bottle");
        if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) || !uri.IsFile) return;
        string? file = IsManifest ? uri.LocalPath : ProjectManifest.Find(uri.LocalPath);
        if (file == null) return;
        try {
            string text = IsManifest ? Source.Text : ManifestText(file);
            var manifest = ProjectManifest.Parse(file, text);
            _ = manifest.SourceFiles();
            _ = ProjectInspector.Libraries(manifest);
            foreach (var asset in manifest.Assets) {
                string path = Path.GetFullPath(Path.Combine(manifest.Root, asset.Source));
                if (!File.Exists(path) && !Directory.Exists(path)) throw new InvalidDataException($"Asset does not exist: {asset.Source}");
            }
        } catch (ManifestException e) {
            foreach (var issue in e.Issues) Diagnostics.Add(new Diagnostic(Source.Range(IsManifest ? issue.Offset : 0, IsManifest ? issue.Length : 1), IsManifest ? issue.Message : $"{file}: {issue.Message}", source: "aquarius-bottle"));
        } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) {
            Diagnostics.Add(new Diagnostic(Source.Range(0, 1), e.Message, source: "aquarius-bottle"));
        }
    }

    private string ManifestText(string file) => resolveDocument?.Invoke(new System.Uri(file).AbsoluteUri)?.Source.Text ?? File.ReadAllText(file);

    private void AddError(string message, Token token, int severity = 1)
    {
        var diagnostic = new Diagnostic(Source.Range(token.Start, token.Length), message, severity);
        if (!Diagnostics.Contains(diagnostic) && Diagnostics.Count < 100) Diagnostics.Add(diagnostic);
    }

    private int End(BlockStatement? body) => body == null ? Source.Text.Length
        : blockEnds.GetValueOrDefault(body.Token.Start, Source.Text.Length);
    // A struct declaration ends at the brace closing its fields.
    private int End(StructStatement shape) =>
        tokens.FirstOrDefault(t => t.Start > shape.Name.Token.Start && t.Type == TokenType.LBRACE) is { Type: TokenType.LBRACE } brace
            ? blockEnds.GetValueOrDefault(brace.Start, Source.Text.Length) : shape.Name.Token.Start + shape.Name.Token.Length;

    private Scope Child(int start, int end, Scope parent)
    {
        var scope = new Scope(start, end, parent);
        scopes.Add(scope);
        return scope;
    }

    private void Declare(Identifier? identifier, Scope scope, int kind, string detail, int? end = null, IExpression? value = null, StructStatement? shape = null)
    {
        if (identifier == null || identifier.Token.Type != TokenType.IDENT) return;
        var declaration = new Declaration(identifier.Token, scope, kind, detail, end ?? identifier.Token.Start + identifier.Token.Length, value, identifier.Type, shape);
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
                var function = let.Value as FunctionLiteral ?? (let.Value as AsyncFunctionExpression)?.Literal;
                string names = let.Alias == null ? let.Name?.Value ?? "" : $"{let.Name?.Value} / {let.Alias.Value}";
                string detail = function == null ? $"變數 {names}{Annotation(let.Name?.Type)}" : $"{names}{Signature(function)}";
                Declare(let.Name, scope, function == null ? 13 : 12, detail, function == null ? null : End(function.Body), let.Value);
                Declare(let.Alias, scope, 12, detail, function == null ? null : End(function.Body), let.Value);
                Walk(let.Value, scope);
                break;
            case FunctionLiteral fn:
                var fnScope = Child(fn.Token.Start, End(fn.Body), scope);
                foreach (var parameter in fn.Parameters ?? []) Declare(parameter, fnScope, 13, $"參數 {parameter.Value}{Annotation(parameter.Type)}");
                Walk(fn.Body, fnScope);
                break;
            case ForLoopLiteral loop:
                var loopScope = Child(loop.Token.Start, End(loop.Body), scope);
                Walk(loop.DeclareStatement, loopScope);
                Walk(loop.ConditionalExpression, loopScope);
                Walk(loop.ValueChangeStatement, loopScope);
                Walk(loop.Body, loopScope);
                break;
            case StructStatement shape:
                Declare(shape.Name, scope, StructKind, shape.String(), End(shape), shape: shape);
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

    // Optional annotations as written (": 整數", ": double[]", ": 陣列").
    private static string Annotation(TypeAnnotation? type) => type == null ? "" : ": " + type.String();
    private static string Signature(FunctionLiteral function) =>
        $"({string.Join(", ", (function.Parameters ?? []).Select(p => p.Value + Annotation(p.Type)))}){Annotation(function.ReturnType)}{(function.IsAsync ? " → 承諾 Promise (非同步 async)" : "")}";
    private static FunctionLiteral? FunctionOf(IExpression? value) => value as FunctionLiteral ?? (value as AsyncFunctionExpression)?.Literal;

    // LSP symbol kinds of struct types and their fields.
    private const int StructKind = 23, FieldKind = 8;

    // A type name in annotation position: the token after an annotation's colon (or that colon itself), not a hash
    // literal's. An annotation follows a declared name (變數 x:), a parameter (函式(a, b:), a parameter list (函式(...):)
    // or a struct field (結構 名稱 { 欄位:).
    private bool IsTypePosition(Token token)
    {
        int index = tokens.FindIndex(t => t.Start == token.Start);
        int colon = token.Type == TokenType.COLON ? index : index - 1;
        if (colon < 1 || tokens[colon].Type != TokenType.COLON) return false;
        var before = tokens[colon - 1];
        if (before.Type == TokenType.RPAREN) return true;
        if (before.Type != TokenType.IDENT || colon < 2) return false;
        if (tokens[colon - 2].Type == TokenType.LET) return true;
        // A parameter: the innermost open parenthesis belongs to a function literal.
        int depth = 0;
        for (int i = colon - 2; i >= 0; i--) {
            if (tokens[i].Type is TokenType.RPAREN or TokenType.RBRACE or TokenType.RBRACKET) depth++;
            else if (tokens[i].Type is TokenType.LPAREN or TokenType.LBRACE or TokenType.LBRACKET) {
                if (depth-- > 0) continue;
                if (tokens[i].Type == TokenType.LBRACE)
                    return i > 1 && tokens[i - 1].Type == TokenType.IDENT && tokens[i - 2].Literal is "結構" or "struct";
                return tokens[i].Type == TokenType.LPAREN && i > 0 && tokens[i - 1].Type == TokenType.FUNCTION;
            }
        }
        return false;
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
            // The language's portable modules (Array, Hash, String, Thread) are globals.
            if (declaration == null && LanguageFacts.PortableModules.FirstOrDefault(m => m.Names.Contains(identifier.Value)) is { } portable)
                return new ModuleInfo(Module: portable);
            return declaration == null ? null : ResolveModule(declaration.Value, declaration.Scope, declaration.Token.Start, depth + 1);
        }
        if (expression is CallExpression call && call.Function is Identifier importer &&
            importer.Value is "匯入" or "import" && call.Arguments is [var pathExpression] && Lookup(importer.Value, scope, offset) == null)
        {
            string? path = ImportPath(pathExpression, scope, offset);
            if (path == null) return null;
            if (Catalog.Module(path) is { } module) return new ModuleInfo(Module: module);
            if (resolveDocument == null || !System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            try
            {
                // An Aquarius-source library module (by name), a Bottle library script, or a script beside this one.
                string? file = Catalog.SourceModule(path) ?? (path.Replace('\\', '/').StartsWith("/bottles/", StringComparison.OrdinalIgnoreCase)
                    ? ProjectInspector.ResolveImport(uri.LocalPath, path, ManifestText)
                    : path.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(uri.LocalPath)!, path)) : null);
                if (file == null) return null;
                var imported = resolveDocument(new System.Uri(file).AbsoluteUri);
                return imported == null ? null : new ModuleInfo(Script: imported);
            }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) { return null; }
        }
        if (expression is InfixExpression { Operator: "." } member)
        {
            var receiver = ResolveModule(member.Left, scope, offset, depth + 1);
            string? name = member.Right is Identifier id ? id.Value : (member.Right as CallExpression)?.Function is Identifier fn ? fn.Value : null;
            // Native results carry no static type in the runtime's catalog, so members of call results stay unknown.
            if (receiver?.Script != null && name != null && member.Right is Identifier)
            {
                var declaration = receiver.Script.Exports().LastOrDefault(d => d.Token.Literal == name);
                return declaration == null ? null : receiver.Script.ResolveModule(declaration.Value, declaration.Scope, declaration.Token.Start, depth + 1);
            }
        }
        return null;
    }

    // The struct type of a value, when its declaration shows it: an annotation naming a struct, a construction of one,
    // or a field annotated with one.
    private StructStatement? StructOf(IExpression? expression, Scope scope, int offset, int depth = 0)
    {
        if (depth > 32) return null;
        string? name = null;
        if (expression is Identifier identifier && Lookup(identifier.Value, scope, offset) is { } declaration) {
            name = declaration.Type?.StructName;
            if (name == null && declaration.Value is CallExpression { Function: Identifier constructor }) name = constructor.Value;
            if (name == null && declaration.Value is Identifier or InfixExpression) return StructOf(declaration.Value, declaration.Scope, declaration.Token.Start, depth + 1);
        } else if (expression is CallExpression { Function: Identifier constructor }) name = constructor.Value;
        else if (expression is InfixExpression { Operator: ".", Right: Identifier field } member)
            name = StructOf(member.Left, scope, offset, depth + 1)?.Fields.FirstOrDefault(f => f.Value == field.Value)?.Type?.StructName;
        return name == null ? null : declarations.LastOrDefault(d => d.Struct != null && d.Token.Literal == name)?.Struct;
    }

    private StructStatement? MemberStruct(Token token, int offset)
    {
        int index = tokens.FindIndex(t => t.Start == token.Start);
        int dot = token.Type == TokenType.DOT ? index : index - 1;
        if (dot < 1 || tokens[dot].Type != TokenType.DOT) return null;
        if (memberReceivers.TryGetValue(tokens[dot].Start, out var member)) return StructOf(member.Receiver, member.Scope, offset);
        var previous = tokens[dot - 1];
        return previous.Type == TokenType.IDENT ? StructOf(new Identifier(previous, previous.Literal), ScopeAt(offset), offset) : null;
    }

    private static string FieldDetail(StructStatement shape, Identifier field) => $"欄位 field {shape.Name.Value}.{field.Value}{Annotation(field.Type)}";

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
        if (module?.Module != null)
            return module.Module.Members.SelectMany(m => m.Names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .Select(n => (object)new { label = n, kind = m.Function ? 3 : 5, detail = m.Detail }));
        return module?.Script?.Exports().Where(d => d.Token.Literal.StartsWith(prefix, StringComparison.Ordinal))
            .Select(d => (object)new { label = d.Token.Literal, kind = d.Kind == 12 ? 3 : 6, detail = d.Detail }) ?? [];
    }

    public object Completion(Position position)
    {
        if (IsManifest) return new[] { "manifest_version", "project", "library", "name", "type", "version", "description", "license", "entry", "sources", "platforms", "libraries",
                "native", "modules", "names", "android", "permissions", "dependencies", "path", "git", "rev", "subdirectory", "assets", "source", "target", "onnx", "cuda", "desktop", "hide_terminal" }
            .Select(label => new { label, kind = 10, detail = "Aquarius project manifest" }).ToArray();
        int offset = Source.Offset(position);
        if (!Source.IsCode(offset)) return Array.Empty<object>();
        var token = tokens.LastOrDefault(t => t.Start < offset && offset <= t.Start + t.Length);
        string prefix = token.Type == TokenType.IDENT || LanguageFacts.Keywords.ContainsKey(token.Literal ?? "")
            ? Source.Text[token.Start..offset] : "";
        if (token.Type == TokenType.DOT || (token.Type == TokenType.IDENT && IsMember(token))) {
            if (MemberStruct(token, offset) is { } shape)
                return shape.Fields.Where(f => f.Value.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(f => (object)new { label = f.Value, kind = 5, detail = FieldDetail(shape, f) }).ToArray();
            return MemberCompletion(MemberModule(token, offset), prefix).ToArray();
        }
        // After an annotation's colon, only type names are valid: the built-in ones and the file's struct types.
        if (token.Type == TokenType.COLON || (token.Type == TokenType.IDENT && IsTypePosition(token)))
            return LanguageFacts.TypeNames.Select(t => (Label: t.Key, Kind: 25, Detail: t.Value))
                .Concat(declarations.Where(d => d.Struct != null).Select(d => (Label: d.Token.Literal, Kind: 22, Detail: d.Detail)))
                .Where(t => t.Label.StartsWith(prefix, StringComparison.Ordinal))
                .Select(t => (object)new { label = t.Label, kind = t.Kind, detail = t.Detail }).ToArray();
        var items = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in LanguageFacts.Keywords)
            items[entry.Key] = new { label = entry.Key, kind = 14, detail = entry.Value };
        foreach (var entry in Catalog.Globals)
            items[entry.Key] = new { label = entry.Key, kind = 3, detail = entry.Value };
        foreach (var entry in LanguageFacts.Builtins)
            items[entry.Key] = new { label = entry.Key, kind = entry.Key is "目前工作目錄" or "currWorkingDir" ? 6 : 3, detail = entry.Value };
        foreach (var declaration in Visible(offset))
            items[declaration.Token.Literal] = new { label = declaration.Token.Literal, kind = declaration.Kind switch { 12 => 3, StructKind => 22, _ => 6 }, detail = declaration.Detail };
        return items.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(item => item.Value).ToArray();
    }

    public object? Hover(Position position)
    {
        int offset = Source.Offset(position);
        if (At(offset) is not Token token) return null;
        if (token.Type == TokenType.STRING || token.Type == TokenType.ILLEGAL) return null;
        if (IsMember(token))
        {
            if (MemberStruct(token, offset) is { } shape && shape.Fields.FirstOrDefault(f => f.Value == token.Literal) is { } field)
                return new { contents = new { kind = "plaintext", value = FieldDetail(shape, field) }, range = Source.Range(token.Start, token.Length) };
            var module = MemberModule(token, offset);
            string? memberDetail = module?.Module != null ? module.Module.Find(token.Literal)?.Detail
                : module?.Script?.Exports().LastOrDefault(d => d.Token.Literal == token.Literal)?.Detail;
            return memberDetail == null ? null : new { contents = new { kind = "plaintext", value = memberDetail }, range = Source.Range(token.Start, token.Length) };
        }
        string? detail = null;
        if (IsTypePosition(token)) LanguageFacts.TypeNames.TryGetValue(token.Literal, out detail);
        detail ??= Visible(offset).FirstOrDefault(d => d.Token.Literal == token.Literal)?.Detail;
        // Struct types are known in the whole file: annotations may name one declared further down.
        detail ??= declarations.LastOrDefault(d => d.Struct != null && d.Token.Literal == token.Literal)?.Detail;
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
            if (MemberStruct(token, offset) is { } shape && shape.Fields.FirstOrDefault(f => f.Value == token.Literal) is { } field)
                return new { uri = Uri, range = Source.Range(field.Token.Start, field.Token.Length) };
            var imported = MemberModule(token, offset)?.Script;
            var member = imported?.Exports().LastOrDefault(d => d.Token.Literal == token.Literal);
            return member == null ? null : new { uri = imported!.Uri, range = imported.Source.Range(member.Token.Start, member.Token.Length) };
        }
        var declaration = Visible(offset).FirstOrDefault(d => d.Token.Literal == token.Literal)
            ?? declarations.LastOrDefault(d => d.Struct != null && d.Token.Literal == token.Literal);
        return declaration == null ? null : new { uri = Uri, range = Source.Range(declaration.Token.Start, declaration.Token.Length) };
    }

    /// <summary>The signature of the call the position is inside: a declared function, a struct construction, a module
    /// member (portable or native) or a core global, with the argument the position is in.</summary>
    public object? SignatureHelp(Position position)
    {
        int offset = Source.Offset(position);
        if (IsManifest || !Source.IsCode(offset)) return null;
        // The innermost parenthesis still open at the position, and the commas before it at its own depth.
        int depth = 0, commas = 0, open = -1;
        for (int i = tokens.Count - 1; i >= 0; i--) {
            var t = tokens[i];
            if (t.Type == TokenType.EOF || t.Start >= offset) continue;
            if (t.Type is TokenType.RPAREN or TokenType.RBRACKET or TokenType.RBRACE) depth++;
            else if (t.Type is TokenType.LPAREN or TokenType.LBRACKET or TokenType.LBRACE) {
                if (depth == 0) { if (t.Type == TokenType.LPAREN) open = i; break; }
                depth--;
            }
            else if (t.Type == TokenType.COMMA && depth == 0) commas++;
        }
        if (open < 1 || tokens[open - 1].Type != TokenType.IDENT) return null;
        var callee = tokens[open - 1];
        if (CallLabel(callee, offset) is not string label) return null;
        int start = label.IndexOf('('), end = label.LastIndexOf(')');
        if (start < 0 || end < start) return null;
        var parameters = new List<object>();
        for (int p = start + 1; p < end;) {
            while (p < end && label[p] == ' ') p++;
            int stop = label.IndexOf(", ", p, end - p, StringComparison.Ordinal);
            if (stop < 0) stop = end;
            if (stop > p) parameters.Add(new { label = new[] { p, stop } });
            p = stop + 2;
        }
        return new
        {
            signatures = new[] { new { label, parameters } },
            activeSignature = 0,
            activeParameter = parameters.Count == 0 ? 0 : Math.Min(commas, parameters.Count - 1)
        };
    }

    // The signature text of a callee name: the part of its description up to " — ", and of "中文(...) / English(...)"
    // the language the call uses.
    private string? CallLabel(Token callee, int offset)
    {
        string name = callee.Literal;
        string? detail;
        if (IsMember(callee)) {
            var module = MemberModule(callee, offset);
            var exported = module?.Script?.Exports().LastOrDefault(d => d.Token.Literal == name);
            if (FunctionOf(exported?.Value) is { } exportedFunction) return name + Signature(exportedFunction);
            detail = module?.Module?.Find(name)?.Detail;
        } else {
            var declaration = Visible(offset).FirstOrDefault(d => d.Token.Literal == name)
                ?? declarations.LastOrDefault(d => d.Struct != null && d.Token.Literal == name);
            if (FunctionOf(declaration?.Value) is { } function) return name + Signature(function);
            if (declaration?.Struct is { } shape) return $"{name}({string.Join(", ", shape.Fields.Select(f => f.Value + Annotation(f.Type)))})";
            if (declaration != null) return null;
            detail = LanguageFacts.Builtins.GetValueOrDefault(name) ?? Catalog.Globals.GetValueOrDefault(name);
        }
        if (detail == null) return null;
        int dash = detail.IndexOf(" — ", StringComparison.Ordinal);
        string label = dash < 0 ? detail : detail[..dash];
        var parts = label.Split(" / ");
        return parts.Count(part => part.Contains('(')) > 1 ? parts.FirstOrDefault(part => part.Contains(name + "(")) ?? parts[0] : label;
    }

    public object Symbols() => declarations.Select(d => (object)new
    {
        name = d.Token.Literal, kind = d.Kind,
        location = new { uri = Uri, range = Source.Range(d.Token.Start, d.End - d.Token.Start) },
        containerName = d.Scope.Parent == null ? "" : "local"
    }).Concat(declarations.Where(d => d.Struct != null).SelectMany(d => d.Struct!.Fields.Select(f => (object)new
    {
        name = f.Value, kind = FieldKind,
        location = new { uri = Uri, range = Source.Range(f.Token.Start, f.Token.Length) },
        containerName = d.Token.Literal
    }))).ToArray();
}
