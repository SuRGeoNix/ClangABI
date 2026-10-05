namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    static string EscapeXml(string value)
        => SecurityElement.Escape(value);

    static void WriteXmlDoc(StringBuilder sb, string indent, string tag, string value, string? attributes = null)
    {
        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        var open = attributes == null
            ? $"<{tag}>"
            : $"<{tag} {attributes}>";

        if (lines.Length == 1)
        {
            sb.AppendLine(
                $"{indent}/// {open}{EscapeXml(lines[0])}</{tag}>");

            return;
        }

        sb.AppendLine($"{indent}/// {open}");

        foreach (var line in lines)
        {
            if (line.Length == 0)
                sb.AppendLine($"{indent}///");
            else
                sb.AppendLine($"{indent}/// {EscapeXml(line)}");
        }

        sb.AppendLine($"{indent}/// </{tag}>");
    }

    static void WriteBriefComment(string comment, StringBuilder sb, string indent)
    {
        var lines = comment
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        if (lines.Length == 1)
        {
            sb.AppendLine($"{indent}/// <summary>{EscapeXml(lines[0])}</summary>");
            return;
        }

        sb.AppendLine($"{indent}/// <summary>");

        foreach (var line in lines)
        {
            var text = EscapeXml(line.TrimEnd());

            if (text.Length == 0)
                sb.AppendLine($"{indent}///");
            else
                sb.AppendLine($"{indent}/// {text}");
        }

        sb.AppendLine($"{indent}/// </summary>");
    }

    string? GetBriefComment(CXCursor cursor)
    {
        if (opts.EmitComments)
            return null;

        using var briefStr = cursor.BriefCommentText;
        var brief = briefStr.ToString();
        if (brief.Trim() == "")
            return null;

        return brief;
    }

    string? GetReturnComment(CXCursor cursor)
    {
        if (opts.EmitComments)
            return null;

        var parsed = cursor.ParsedComment;

        for (uint i = 0; i < parsed.NumChildren; i++)
        {
            var child = parsed.GetChild(i);

            if (child.Kind != CXCommentKind.CXComment_BlockCommand)
                continue;

            using var commandStr = child.BlockCommandComment_CommandName;
            var command = commandStr.ToString();

            if (command is not ("return" or "returns"))
                continue;

            var comment = GetCommentText(child.BlockCommandComment_Paragraph);

            return string.IsNullOrEmpty(comment)
                ? null
                : comment;
        }

        return null;
    }

    string? GetObsolete(CXCursor cursor)
    {
        if (opts.EmitObsolete)
            return null;

        if (cursor.Availability != CXAvailabilityKind.CXAvailability_Deprecated)
            return null;

        // Prefer @deprecated documentation.
        var parsed = cursor.ParsedComment;

        for (uint i = 0; i < parsed.NumChildren; i++)
        {
            var child = parsed.GetChild(i);

            if (child.Kind != CXCommentKind.CXComment_BlockCommand)
                continue;

            using var commandStr = child.BlockCommandComment_CommandName;

            if (commandStr.ToString() != "deprecated")
                continue;

            var message = GetCommentText(child.BlockCommandComment_Paragraph);

            if (!string.IsNullOrEmpty(message))
                return message;
        }

        // Fall back to the compiler deprecation attribute message.
        int alwaysDeprecated;
        int alwaysUnavailable;
        CXString deprecatedMessage;
        CXString unavailableMessage;

        _ = clang.getCursorPlatformAvailability(
            cursor,
            &alwaysDeprecated,
            &deprecatedMessage,
            &alwaysUnavailable,
            &unavailableMessage,
            null,
            0);

        using (deprecatedMessage)
        using (unavailableMessage)
        {
            var message = deprecatedMessage.ToString();

            return string.IsNullOrEmpty(message)
                ? ""
                : message;
        }
    }

    void FillParameterComments(CXCursor cursor, List<ParameterDef> parameters)
    {
        if (opts.EmitComments)
            return;

        var parsed = cursor.ParsedComment;
        var paramComments = new Dictionary<string, string>(StringComparer.Ordinal);

        for (uint i = 0; i < parsed.NumChildren; i++)
        {
            var child = parsed.GetChild(i);

            if (child.Kind != CXCommentKind.CXComment_ParamCommand)
                continue;

            using var nameStr = child.ParamCommandComment_ParamName;
            var name = nameStr.ToString();

            var comment = GetCommentText(child.BlockCommandComment_Paragraph);

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(comment))
                paramComments[name] = comment;
        }

        foreach (var param in parameters)
        {
            if (param.RawName == null || !paramComments.TryGetValue(param.RawName, out var comment))
                continue;

            param.BriefComment = comment;
        }
    }

    static string GetCommentText(CXComment comment)
    {
        StringBuilder sb = new();

        for (uint i = 0; i < comment.NumChildren; i++)
        {
            string value;
            var child = comment.GetChild(i);

            if (child.Kind == CXCommentKind.CXComment_Text)
            {
                using var text = child.TextComment_Text;
                value = text.ToString().Trim();
            }
            else
                value = GetCommentText(child).Trim();

            if (value.Length == 0)
                continue;

            if (sb.Length > 0)
                sb.Append(' ');

            sb.Append(value);
        }

        return sb.ToString();
    }

    string? GetMacroComment(CXCursor cursor)
    {
        if (opts.EmitComments)
            return null;

        CXFile file;
        uint startLine, startColumn, startOffset;
        uint endLine, endColumn, endOffset;

        clang.getExpansionLocation(
            cursor.Extent.Start,
            (void**)&file,
            &startLine,
            &startColumn,
            &startOffset);

        clang.getExpansionLocation(
            cursor.Extent.End,
            null,
            &endLine,
            &endColumn,
            &endOffset);

        if (file.Handle == 0)
            return null;

        using var fileName = clang.getFileName(file);
        var path = fileName.ToString();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        var text = File.ReadAllText(path);

        if (startOffset > text.Length || endOffset > text.Length)
            return null;

        // Prefer a trailing comment on the macro's final physical line.
        var inline = GetInlineMacroComment(text, (int)endOffset);
        if (inline != null)
            return inline;

        return GetLeadingMacroComment(text, (int)startOffset);
    }

    static string? GetInlineMacroComment(string text, int offset)
    {
        var end = offset;

        while (end < text.Length && text[end] is ' ' or '\t')
            end++;

        if (end >= text.Length || text[end] is '\r' or '\n')
            return null;

        if (text.AsSpan(end).StartsWith("///<") ||
            text.AsSpan(end).StartsWith("//!<") ||
            text.AsSpan(end).StartsWith("//"))
        {
            var lineEnd = end;

            while (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n')
                lineEnd++;

            return CleanMacroComment(text[end..lineEnd]);
        }

        if (text.AsSpan(end).StartsWith("/**<") ||
            text.AsSpan(end).StartsWith("/*!<") ||
            text.AsSpan(end).StartsWith("/*"))
        {
            var close = text.IndexOf("*/", end, StringComparison.Ordinal);

            if (close >= 0)
                return CleanMacroComment(text[end..(close + 2)]);
        }

        return null;
    }

    static string? GetLeadingMacroComment(string text, int offset)
    {
        var lineStart = offset;

        while (lineStart > 0 && text[lineStart - 1] is not '\r' and not '\n')
            lineStart--;

        var pos = lineStart;

        // Whitespace immediately before the #define is fine.
        while (pos > 0 && text[pos - 1] is ' ' or '\t')
            pos--;

        // Move over exactly the preceding newline.
        if (pos > 0 && text[pos - 1] == '\n')
        {
            pos--;

            if (pos > 0 && text[pos - 1] == '\r')
                pos--;
        }
        else
        {
            return null;
        }

        // Don't associate across an empty line.
        var previousLineStart = pos;

        while (previousLineStart > 0 &&
               text[previousLineStart - 1] is not '\r' and not '\n')
            previousLineStart--;

        var previousLine = text[previousLineStart..pos].Trim();

        if (previousLine.Length == 0)
            return null;

        // Consecutive // comments.
        if (previousLine.StartsWith("//", StringComparison.Ordinal))
        {
            var start = previousLineStart;

            while (start > 0)
            {
                var end = start;

                if (end > 0 && text[end - 1] == '\n')
                    end--;
                if (end > 0 && text[end - 1] == '\r')
                    end--;

                var begin = end;
                while (begin > 0 && text[begin - 1] is not '\r' and not '\n')
                    begin--;

                var line = text[begin..end].Trim();

                if (!line.StartsWith("//", StringComparison.Ordinal))
                    break;

                start = begin;
            }

            return CleanMacroComment(text[start..pos]);
        }

        // Block comment ending immediately before the macro.
        if (previousLine.EndsWith("*/", StringComparison.Ordinal))
        {
            var close = text.LastIndexOf("*/", pos, StringComparison.Ordinal);
            if (close < 0)
                return null;

            var open = text.LastIndexOf("/*", close, StringComparison.Ordinal);
            if (open < 0)
                return null;

            // Only whitespace may exist between the comment and #define.
            if (!string.IsNullOrWhiteSpace(text[(close + 2)..lineStart]))
                return null;

            return CleanMacroComment(text[open..(close + 2)]);
        }

        return null;
    }

    static string? CleanMacroComment(string value)
    {
        value = value.Trim();

        if (value.StartsWith("/*", StringComparison.Ordinal))
        {
            value = value[2..];

            if (value.StartsWith('*') || value.StartsWith('!'))
                value = value[1..];

            if (value.EndsWith("*/", StringComparison.Ordinal))
                value = value[..^2];
        }

        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(x =>
            {
                var line = x.Trim();

                if (line.StartsWith("//", StringComparison.Ordinal))
                    line = line[2..].TrimStart();

                if (line.StartsWith('*'))
                    line = line[1..].TrimStart();

                if (line.StartsWith('<'))
                    line = line[1..].TrimStart();

                return line;
            })
            .Select(CleanDoxygenLine)
            .Where(x => x != null)
            .ToArray();

        // Remove empty lines at start/end, preserve them internally.
        int start = 0;
        int end   = lines.Length;

        while (start < end && lines[start]!.Length == 0)
            start++;

        while (end > start && lines[end - 1]!.Length == 0)
            end--;

        if (start == end)
            return null;

        return string.Join("\n", lines[start..end]!);
    }

    static string? CleanDoxygenLine(string line)
    {
        // Pure Doxygen grouping markers.
        if (line is "@{" or "@}" or "\\{" or "\\}")
            return null;

        if (line.StartsWith("@ingroup ", StringComparison.Ordinal) ||
            line.StartsWith("\\ingroup ", StringComparison.Ordinal))
            return null;

        // Keep useful content, remove the directive.
        if (line.StartsWith("@brief ", StringComparison.Ordinal))
            return line[7..].TrimStart();

        if (line.StartsWith("\\brief ", StringComparison.Ordinal))
            return line[7..].TrimStart();

        if (line.StartsWith("@note ", StringComparison.Ordinal))
            return "Note: " + line[6..].TrimStart();

        if (line.StartsWith("\\note ", StringComparison.Ordinal))
            return "Note: " + line[6..].TrimStart();

        if (line.StartsWith("@warning ", StringComparison.Ordinal))
            return "Warning: " + line[9..].TrimStart();

        if (line.StartsWith("\\warning ", StringComparison.Ordinal))
            return "Warning: " + line[9..].TrimStart();

        if (line.StartsWith("@defgroup ", StringComparison.Ordinal) ||
            line.StartsWith("\\defgroup ", StringComparison.Ordinal))
        {
            var rest  = line[10..].TrimStart();
            var space = rest.IndexOf(' ');

            return space >= 0
                ? rest[(space + 1)..].TrimStart()
                : null;
        }

        return line;
    }
}
