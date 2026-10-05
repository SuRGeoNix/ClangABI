namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    void ProcessMacro(CXCursor cursor)
    {
        var tokens = GetMacroTokens(cursor);
        if (tokens.Count == 0)
            return;

        var def = new MacroDef(GetSpelling(cursor))
        {
            IsFunctionLike = cursor.IsMacroFunctionLike,
            BriefComment   = GetMacroComment(cursor),
            Obsolete       = GetObsolete(cursor)
        };

        int index = 1; // skip macro name

        if (def.IsFunctionLike)
        {
            if (index >= tokens.Count || tokens[index].Text != "(")
                throw new InvalidOperationException($"Invalid macro parameter list: {def.RawName}");

            index++; // skip '('

            while (index < tokens.Count && tokens[index].Text != ")")
            {
                var token = tokens[index].Text;

                if (token == "...")
                    def.IsVariadic = true;
                else if (token != ",")
                    def.Parameters.Add(new(token));

                index++;
            }

            if (index >= tokens.Count)
                throw new InvalidOperationException($"Unterminated macro parameter list: {def.RawName}");

            index++; // skip ')'
        }

        for (; index < tokens.Count; index++)
            def.Tokens.Add(tokens[index]);

        if (def.Tokens.Count != 0)
            macros.Add(def);
    }

    int parsed      = 0;
    int unsupported = 0;
    int failed      = 0;
    int manual      = 0;

    void ParseMacros()
    {
        foreach (var macro in Macros)
        {
            var reason = GetUnsupportedMacroReason(macro);
            if (reason != null)
            {
                macro.ParseError = reason;
                unsupported++;
                continue;
            }

            try
            {
                macro.Expression = new MacroParser(this, macro.Tokens).Parse();
                opts.PostProcessMacro?.Invoke(macro);
                parsed++;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or MacroParseException)
            {
                macro.ParseError = ex.Message;
                failed++;

                if (failed <= 10)
                {
                    Console.WriteLine(
                        $"{macro.RawName}: {ex.Message}\n" +
                        $"  {string.Join(" ", macro.Tokens.Select(x => x.Text))}");
                }
            }
        }

        //var unsupported2 = Macros
        //    .Where(x => x.ParseError != null)
        //    .GroupBy(x => x.ParseError)
        //    .Select(x => new { Reason = x.Key, Count = x.Count() });

        //foreach (var x in unsupported2)
        //    Console.WriteLine($"{x.Count,4}  {x.Reason}");
    }

    static string? GetUnsupportedMacroReason(MacroDef macro)
    {
        if (macro.Tokens.Count == 0)
            return "Empty macro.";

        if (macro.Tokens[0].Text == ".")    // Designated initializer fragment: .formats.query_func = ...
            return "Designated initializer.";

        foreach (var token in macro.Tokens)
        {
            switch (token.Text)
            {
                case "{":
                case "}":
                    return "Initializer/compound literal.";

                case "#":
                case "##":
                    return "Stringification/token pasting.";

                case "sizeof":
                case "_Alignof":
                case "alignof":
                case "__alignof__":
                    return "Size/alignment expression.";

                case "_Generic":
                    return "Generic selection.";
            }
        }

        return null;
    }

    List<MacroToken> GetMacroTokens(CXCursor cursor)
    {
        var tu      = cursor.TranslationUnit;
        var range   = cursor.Extent;
        uint count  = 0;

        CXToken* tokens = null;
        clang.tokenize(tu, range, &tokens, &count);

        try
        {
            var result = new List<MacroToken>((int)count);

            for (uint i = 0; i < count; i++)
            {
                if (tokens[i].Kind == CXTokenKind.CXToken_Comment)
                    continue;

                using var spelling = clang.getTokenSpelling(tu, tokens[i]);
                result.Add(new(tokens[i].Kind, spelling.ToString()));
            }

            return result;
        }
        finally
        {
            if (tokens != null)
                clang.disposeTokens(tu, tokens, count);
        }
    }
}
