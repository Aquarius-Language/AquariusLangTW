using System.Globalization;
using System.Text.RegularExpressions;

namespace AquariusLang.Bottle;

/// <summary>SemVer precedence; build metadata never affects version selection.</summary>
public sealed record BottleVersion(int Major, int Minor, int Patch, string PreRelease = "", string Metadata = "") : IComparable<BottleVersion> {
    public static BottleVersion Parse(string text, bool abbreviated = false) {
        var match = Regex.Match(text, @"^(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?(?:\.(0|[1-9][0-9]*))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$");
        if (!match.Success || (!abbreviated && !match.Groups[3].Success)) throw new InvalidDataException($"Invalid semantic version '{text}'; use major.minor.patch (optional -prerelease/+metadata).");
        int Number(int group) => match.Groups[group].Success && int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : match.Groups[group].Success ? throw new InvalidDataException($"Version component too large: {text}") : 0;
        string pre = match.Groups[4].Value;
        if (pre.Split('.').Any(p => p.Length > 1 && p.All(char.IsAsciiDigit) && p[0] == '0')) throw new InvalidDataException($"Invalid prerelease version: {text}");
        return new(Number(1), Number(2), Number(3), pre, match.Groups[5].Value);
    }
    public int CompareTo(BottleVersion? other) {
        if (other == null) return 1;
        int result = Major.CompareTo(other.Major); if (result != 0) return result;
        result = Minor.CompareTo(other.Minor); if (result != 0) return result;
        result = Patch.CompareTo(other.Patch); if (result != 0) return result;
        if (PreRelease == other.PreRelease) return 0;
        if (PreRelease.Length == 0) return 1; if (other.PreRelease.Length == 0) return -1;
        string[] left = PreRelease.Split('.'), right = other.PreRelease.Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++) {
            bool ln = left[i].All(char.IsAsciiDigit), rn = right[i].All(char.IsAsciiDigit);
            result = ln && rn ? left[i].Length.CompareTo(right[i].Length) : ln != rn ? (ln ? -1 : 1) : 0;
            if (result == 0) result = string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (PreRelease.Length == 0 ? "" : "-" + PreRelease) + (Metadata.Length == 0 ? "" : "+" + Metadata);
}

public sealed class VersionRequirement {
    private readonly List<(string Operator, BottleVersion Version)> clauses = [];
    public string Text { get; }
    public VersionRequirement(string text) {
        Text = text;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Dependencies require an explicit version constraint.");
        foreach (string part in text.Split(',')) {
            var match = Regex.Match(part.Trim(), @"^(>=|<=|>|<|==|=)?\s*(\S+)$");
            if (!match.Success) throw new InvalidDataException($"Invalid version constraint: {text}. Use e.g. '>= 0.17, <= 0.21'.");
            clauses.Add((match.Groups[1].Success ? match.Groups[1].Value : "=", BottleVersion.Parse(match.Groups[2].Value, true)));
        }
    }
    public bool Matches(BottleVersion version) =>
        // Prereleases are opt-in and must refer to the same core version.
        (version.PreRelease.Length == 0 || clauses.Any(c => c.Version.PreRelease.Length > 0 && c.Version.Major == version.Major && c.Version.Minor == version.Minor && c.Version.Patch == version.Patch)) &&
        clauses.All(c => c.Operator switch { ">" => version.CompareTo(c.Version) > 0, ">=" => version.CompareTo(c.Version) >= 0, "<" => version.CompareTo(c.Version) < 0, "<=" => version.CompareTo(c.Version) <= 0, _ => version.CompareTo(c.Version) == 0 });
}
