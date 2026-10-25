namespace AquariusLanguageServer;

internal readonly record struct Position(int line, int character);
internal readonly record struct TextRange(Position start, Position end);

internal sealed class SourceText
{
    public string Text { get; }
    private readonly List<int> starts = [0];
    private readonly List<int> ends = [];

    public SourceText(string text)
    {
        Text = text;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n') continue;
            ends.Add(i);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            starts.Add(i + 1);
        }
        ends.Add(text.Length);
    }

    public int Offset(Position position)
    {
        if (position.line < 0 || position.line >= starts.Count || position.character < 0
            || position.character > ends[position.line] - starts[position.line])
            throw new ArgumentException("Position lies outside the document.");
        return starts[position.line] + position.character;
    }

    public Position At(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        int line = starts.BinarySearch(offset);
        if (line < 0) line = ~line - 1;
        return new Position(line, Math.Min(offset, ends[line]) - starts[line]);
    }

    public TextRange Range(int start, int length) => new(At(start), At(start + Math.Max(0, length)));

    public bool IsCode(int offset)
    {
        // The language currently has literal strings without escape sequences.
        bool quoted = false, block = false, comment = false;
        for (int i = 0; i < offset; i++)
        {
            char c = Text[i];
            if (comment) { if (c == '\r' || c == '\n') comment = false; continue; }
            if (block)
            {
                if (c == '#' && i + 1 < offset && Text[i + 1] == '#') { block = false; i++; }
                continue;
            }
            if (quoted) { if (c == '"') quoted = false; continue; }
            if (c == '"') quoted = true;
            else if (c == '#')
            {
                if (i + 1 < Text.Length && Text[i + 1] == '#') { block = true; i++; }
                else comment = true;
            }
        }
        return !quoted && !block && !comment;
    }
}
