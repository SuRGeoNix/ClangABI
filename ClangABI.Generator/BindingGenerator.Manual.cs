namespace ClangABI.Generator;

public partial class BindingGenerator
{
    sealed record ExistingManualBlock(
        string Kind,
        string Name,
        string Implementation,
        string Hash,
        bool IsEnabled);

    readonly Regex ManualDeclarationRegex = RegexManualDeclaration();

    [GeneratedRegex(@"(?m)^[ \t]*(?<signature>public\s+static\s+(?<type>\S+)\s+(?<name>[A-Za-z_]\w*)\s*(?<params>\([^)]*\))?)", RegexOptions.Compiled)]
    private static partial Regex RegexManualDeclaration();

    ExistingManualBlock[] LoadManualBlocks(string path, string kind)
    {
        if (!File.Exists(path))
            return [];

        var lines  = File.ReadAllLines(path);
        var result = new List<ExistingManualBlock>();

        string? name = null;
        int implementationStart = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (name == null)
            {
                if (TryGetCompactManualBegin(lines[i], kind, out var blockName))
                {
                    name = blockName;
                    implementationStart = i + 1;
                }

                continue;
            }

            // Recover cleanly from a malformed/incomplete block.
            if (TryGetCompactManualBegin(lines[i], kind, out var nextName))
            {
                name = nextName;
                implementationStart = i + 1;
                continue;
            }

            if (!TryGetCompactManualEnd(lines[i], kind, name, out var hash))
                continue;

            AddManualBlock(result, kind, name, lines[implementationStart..i], hash);

            name = null;
            implementationStart = -1;
        }

        return [.. result];
    }

    static void AddManualBlock(
        List<ExistingManualBlock> result,
        string kind,
        string name,
        string[] implementationLines,
        string hash)
    {
        var implementation = NormalizeManualImplementation(string.Join("\n", implementationLines));
        var uncommented     = UncommentManualImplementation(implementation);
        var isEnabled       = uncommented == null;

        result.Add(new(
            kind,
            name,
            isEnabled ? implementation : uncommented!,
            hash,
            isEnabled));
    }

    static bool TryGetCompactManualBegin(string line, string kind, out string name)
    {
        var text   = line.Trim();
        var prefix = $"/* {kind} begin:";

        if (!text.StartsWith(prefix, StringComparison.Ordinal) ||
            !text.EndsWith("*/", StringComparison.Ordinal))
        {
            name = "";
            return false;
        }

        var content   = text[prefix.Length..^2].Trim();
        var separator = content.IndexOf(" | Native:", StringComparison.Ordinal);

        if (separator == -1)
        {
            name = "";
            return false;
        }

        name = content[..separator].Trim();
        return IsManualIdentifier(name);
    }

    static bool TryGetCompactManualEnd(string line, string kind, string expectedName, out string hash)
    {
        var text   = line.Trim();
        var prefix = $"/* {kind} end:";

        if (!text.StartsWith(prefix, StringComparison.Ordinal) ||
            !text.EndsWith("*/", StringComparison.Ordinal))
        {
            hash = "";
            return false;
        }

        var content   = text[prefix.Length..^2].Trim();
        var separator = content.LastIndexOf(" | hash:", StringComparison.Ordinal);

        if (separator == -1 ||
            !string.Equals(content[..separator].Trim(), expectedName, StringComparison.Ordinal))
        {
            hash = "";
            return false;
        }

        hash = content[(separator + " | hash:".Length)..].Trim();
        return hash.Length != 0;
    }

    static bool IsManualIdentifier(string value)
    {
        if (value.Length == 0 || !(value[0] == '_' || char.IsLetter(value[0])))
            return false;

        for (int i = 1; i < value.Length; i++)
            if (!(value[i] == '_' || char.IsLetterOrDigit(value[i])))
                return false;

        return true;
    }

    static string NormalizeManualImplementation(string value)
    {
        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        int minIndent = int.MaxValue;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int indent = 0;
            while (indent < line.Length && line[indent] is ' ' or '\t')
                indent++;

            minIndent = Math.Min(minIndent, indent);
        }

        if (minIndent == int.MaxValue)
            return "";

        return string.Join("\n", lines.Select(x => x.Length >= minIndent ? x[minIndent..] : x)).Trim('\n');
    }

    static string? UncommentManualImplementation(string value)
    {
        var lines  = value.Split('\n');
        var result = new string[lines.Length];

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                result[i] = "";
                continue;
            }

            int index = 0;
            while (index < line.Length && line[index] is ' ' or '\t')
                index++;

            if (index + 1 >= line.Length || line[index] != '/' || line[index + 1] != '/')
                return null;

            index += 2;
            if (index < line.Length && line[index] == ' ')
                index++;

            result[i] = line[index..];
        }

        return string.Join("\n", result);
    }

    static string NormalizeManualNativeSource(string nativeSource)
        => nativeSource.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    static string GetManualHash(string nativeSource)
    {
        // FNV-1a 64-bit is deterministic, tiny, and sufficient for native-change detection.
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime       = 1099511628211UL;

        ulong hash = offsetBasis;
        foreach (var value in Encoding.UTF8.GetBytes(NormalizeManualNativeSource(nativeSource)))
        {
            hash ^= value;
            hash *= prime;
        }

        return hash.ToString("X16");
    }

    static bool ManualHashMatches(ExistingManualBlock existing, string hash)
        => existing.Hash == hash;

    static (string Implementation, bool Commented, bool Reset) ResolveManualBlock(
        ExistingManualBlock? existing,
        string hash,
        string freshImplementation)
    {
        if (existing != null && ManualHashMatches(existing, hash))
            return (existing.Implementation, !existing.IsEnabled, false);

        return (freshImplementation, true, existing != null);
    }

    static void WriteManualBlock(
        StringBuilder sb,
        string kind,
        string name,
        string native,
        string hash,
        string implementation,
        bool commented)
    {
        native = native
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("*/", "* /", StringComparison.Ordinal);

        sb.AppendLine($"\t/* {kind} begin: {name} | Native: {native} */");
        WriteManualImplementation(sb, implementation, commented);
        sb.AppendLine($"\t/* {kind} end: {name} | hash: {hash} */");
        sb.AppendLine();
    }

    static void WriteManualImplementation(StringBuilder sb, string implementation, bool commented)
    {
        var lines = implementation
	        .Replace("\r\n", "\n")
	        .Replace('\r', '\n')
	        .Split('\n')
	        .SkipWhile(string.IsNullOrWhiteSpace)
	        .Reverse()
	        .SkipWhile(string.IsNullOrWhiteSpace)
	        .Reverse();

        foreach (var line in lines)
        {
            if (commented)
                sb.AppendLine(line.Length == 0 ? "\t//" : $"\t// {line}");
            else
                sb.AppendLine($"\t{line}");
        }
    }
}
