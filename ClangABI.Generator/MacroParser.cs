namespace ClangABI.Generator;

internal sealed class MacroParser(BindingGenerator generator, IReadOnlyList<MacroToken> tokens)
{
    int index;

    public MacroExpression Parse()
    {
        if (tokens.Count == 0)
            throw new MacroParseException("Macro has no expression tokens.");

        var expressions = new List<MacroExpression>();

        while (index < tokens.Count)
            expressions.Add(ParseConditional());

        return expressions.Count == 1
            ? expressions[0]
            : new MacroConcatExpression(expressions);
    }

    MacroExpression ParseConditional()
    {
        var condition = ParseBinary(1);
        if (!Match("?"))
            return condition;

        var trueExpression = ParseConditional();
        Expect(":");
        var falseExpression = ParseConditional();
        return new MacroConditionalExpression(condition, trueExpression, falseExpression);
    }

    MacroExpression ParseBinary(int minPrecedence)
    {
        var left = ParseUnary();

        while (index < tokens.Count && GetPrecedence(tokens[index].Text) is int precedence && precedence >= minPrecedence)
        {
            var op = tokens[index++].Text;
            var right = ParseBinary(precedence + 1);
            left = new MacroBinaryExpression(left, op, right);
        }

        return left;
    }

    MacroExpression ParseUnary()
    {
        if (index < tokens.Count && tokens[index].Text is "+" or "-" or "!" or "~")
        {
            var op = tokens[index++].Text;
            return new MacroUnaryExpression(op, ParseUnary());
        }

        if (IsCast())
        {
            index++; // '('
            var type = ReadCastType();
            Expect(")");
            return new MacroCastExpression(type, ParseUnary());
        }

        return ParsePostfix();
    }

    MacroExpression ParsePostfix()
    {
        var expression = ParsePrimary();

        while (index < tokens.Count)
        {
            var op = tokens[index].Text;

            if (op is not "." and not "->")
                break;

            index++;

            if (index >= tokens.Count ||
                tokens[index].Kind is not CXTokenKind.CXToken_Identifier)
                throw new MacroParseException($"Expected member name after '{op}'.");

            var member = tokens[index++].Text;

            expression = new MacroMemberExpression(
                expression,
                member,
                op == "->");
        }

        return expression;
    }

    MacroExpression ParsePrimary()
    {
        if (Match("("))
        {
            var expression = ParseConditional();
            Expect(")");
            return expression;
        }

        if (index >= tokens.Count)
            throw new MacroParseException("Unexpected end of macro expression.");

        var token = tokens[index++];

        if (token.Kind == CXTokenKind.CXToken_Literal)
        {
            var kind = token.Text.StartsWith('"') || token.Text.StartsWith("u8\"", StringComparison.Ordinal)
                ? MacroLiteralKind.String
                : token.Text.StartsWith('\'')
                    ? MacroLiteralKind.Character
                    : MacroLiteralKind.Number;

            return new MacroLiteralExpression(token.Text, kind);
        }

        if (token.Kind is not CXTokenKind.CXToken_Identifier and not CXTokenKind.CXToken_Keyword)
            throw new MacroParseException($"Unexpected token '{token.Text}'.");

        if (!Match("("))
            return new MacroIdentifierExpression(token.Text);

        var arguments = new List<MacroExpression>();
        if (!Match(")"))
        {
            while (true)
            {
                arguments.Add(ParseConditional());

                if (Match(")"))
                    break;

                Expect(",");
            }
        }

        return new MacroCallExpression(token.Text, arguments);
    }

    bool IsCast()
    {
        if (index >= tokens.Count || tokens[index].Text != "(")
            return false;

        int end = index + 1;
        while (end < tokens.Count && tokens[end].Text != ")")
        {
            if (tokens[end].Text is "(" or "," or "?" or ":")
                return false;
            end++;
        }

        if (end >= tokens.Count || end == index + 1 || end + 1 >= tokens.Count)
            return false;

        var type = JoinTypeTokens(index + 1, end);
        return generator.ResolveMacroType(type) != null;
    }

    string ReadCastType()
    {
        int start = index;
        while (index < tokens.Count && tokens[index].Text != ")")
            index++;

        if (index >= tokens.Count)
            throw new MacroParseException("Unterminated cast type.");

        return JoinTypeTokens(start, index);
    }

    string JoinTypeTokens(int start, int end)
    {
        var sb = new StringBuilder();

        for (int i = start; i < end; i++)
        {
            var text = tokens[i].Text;
            if (text == "*")
            {
                if (sb.Length != 0 && sb[^1] != ' ')
                    sb.Append(' ');
                sb.Append('*');
            }
            else
            {
                if (sb.Length != 0 && sb[^1] != ' ')
                    sb.Append(' ');
                sb.Append(text);
            }
        }

        return sb.ToString();
    }

    bool Match(string text)
    {
        if (index >= tokens.Count || tokens[index].Text != text)
            return false;

        index++;
        return true;
    }

    void Expect(string text)
    {
        if (!Match(text))
            throw new MacroParseException($"Expected '{text}'.");
    }

    static int? GetPrecedence(string op) => op switch
    {
        "||"             => 1,
        "&&"             => 2,
        "|"              => 3,
        "^"              => 4,
        "&"              => 5,
        "==" or "!="     => 6,
        "<" or "<=" or ">" or ">=" => 7,
        "<<" or ">>"     => 8,
        "+" or "-"       => 9,
        "*" or "/" or "%" => 10,
        _ => null
    };
}

internal sealed class MacroParseException : Exception
{
    public string? MacroName { get; }

    public MacroParseException(string message)
        : base(message)
    {
    }

    public MacroParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MacroParseException(string macroName, string message)
        : base($"Failed to parse macro '{macroName}': {message}")
    {
        MacroName = macroName;
    }

    public MacroParseException(string macroName, string message, Exception innerException)
        : base($"Failed to parse macro '{macroName}': {message}", innerException)
    {
        MacroName = macroName;
    }
}
