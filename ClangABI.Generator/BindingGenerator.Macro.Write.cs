using System.Globalization;

namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    void WriteMacros()
    {
        var constants = outputDefinitions
            .OfType<MacroConstantDef>()
            .Where(x => !x.SkipOutput && x.Expression != null)
            .OrderBy(x => x.RawName, StringComparer.InvariantCultureIgnoreCase)
            .ToArray();

        var functions = outputDefinitions
            .OfType<MacroFunctionDef>()
            .Where(x => !x.SkipOutput && x.Expression != null)
            .OrderBy(x => x.RawName, StringComparer.InvariantCultureIgnoreCase)
            .ToArray();

        StringBuilder sb = new();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
        }
        
        sb.Append(namespaceLine);
        sb.Append(classNameLine);

        foreach (var def in functions)
            WriteMacroFunction(def, sb);

        foreach (var def in constants)
            WriteMacroConstant(def, sb);

        sb.AppendLine("}");

        File.WriteAllText(outputBase + "macros.g.cs", sb.ToString());
    }

    void WriteManualMacros()
    {
        var existingByName = existingMacrosManual
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

        StringBuilder sb = new();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
        }

        sb.Append(namespaceLine);
        sb.AppendLine("#pragma warning disable IDE1006");
        sb.Append(classNameLine);

        foreach (var macro in Macros.OrderBy(x => x.RawName, StringComparer.InvariantCultureIgnoreCase))
        {
            if (macro.SkipOutput)
                continue;

            existingByName.TryGetValue(macro.RawName, out var existing);

            bool generated = definitionsByRawName.TryGetValue(macro.RawName, out var defs) &&
                             defs.Any(x => x is MacroConstantDef c && c.Expression != null ||
                                           x is MacroFunctionDef f && f.Expression != null);

            if (existing == null && generated)
                continue;

            manual++;
            WriteManualMacro(macro, existing, sb);
        }

        sb.AppendLine("}");

        File.WriteAllText(outputBase + "macros.cs", sb.ToString());

        Console.WriteLine($"Macro parsing: total={Macros.Count}, parsed={parsed}, unsupported={unsupported}, manual={manual}, failed={failed}");
    }

    void WriteManualMacro(MacroDef macro, ExistingManualBlock? existing, StringBuilder sb)
    {
        var source = GetMacroSource(macro);
        var hash   = GetManualHash(source);
        var fresh  = BuildManualMacroTemplate(macro);
        var block  = ResolveManualBlock(existing, hash, fresh);

        if (block.Reset)
            Console.WriteLine($"Manual macro '{macro.RawName}' changed in native headers and was reset.");

        WriteManualBlock(
            sb,
            "macro",
            macro.RawName,
            $"#define {source}",
            hash,
            block.Implementation,
            block.Commented);
    }

    string BuildManualMacroTemplate(MacroDef macro)
    {
        StringBuilder sb = new();

        WriteBriefObsolete(macro, sb);

        TypeRef? inferredType = null;
        if (macro.Expression != null)
            inferredType = GetMacroExpressionType(macro, macro.Expression, new(StringComparer.Ordinal));

        var returnType = inferredType == null ? "TYPE" : GetNativeType(inferredType);

        if (macro.IsFunctionLike)
        {
            var parameters = new List<string>(macro.Parameters.Count + (macro.IsVariadic ? 1 : 0));

            foreach (var parameter in macro.Parameters)
            {
                var type = parameter.Type == null ? "TYPE" : GetNativeType(parameter.Type);
                parameters.Add($"{type} {EscapeIdentifier(parameter.RawName)}");
            }

            if (macro.IsVariadic)
                parameters.Add("params object[] args");

            sb.AppendLine($"public static {returnType} {macro.RawName}({string.Join(", ", parameters)})");
            sb.AppendLine("{");
            sb.AppendLine("\tthrow new NotImplementedException();");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine($"public static {returnType} {macro.RawName}");
            sb.AppendLine("\t=> throw new NotImplementedException();");
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    void WriteMacroConstant(MacroConstantDef def, StringBuilder sb)
    {
        WriteBriefObsolete(def, sb, "\t");

        var type       = GetNativeType(def.Type);
        var expression = WriteMacroExpression(def.Expression!);

        if (def.IsConst)
            sb.AppendLine($"\tpublic const {type} {def.Name} = {expression};");
        else
            // Emit non-constant macros as properties to avoid initialization-order issues and
            // eager evaluation that could invoke av_* P/Invoke methods and load native libraries
            sb.AppendLine($"\tpublic static {type} {def.Name} => {expression};");

        sb.AppendLine();
    }

    void WriteMacroFunction(MacroFunctionDef def, StringBuilder sb)
    {
        var parameters = WriteFunctionHeader(def, sb);
        sb.AppendLine($"\tpublic static {GetNativeType(def.ReturnType)} {def.Name}({parameters}) => {WriteMacroExpression(def.Expression!)};");
        sb.AppendLine();
    }

    string WriteMacroExpression(MacroExpression expression) => expression switch
    {
        MacroRawExpression raw              => raw.Text,
        MacroLiteralExpression literal      => WriteMacroLiteral(literal),
        MacroIdentifierExpression identifier=> WriteMacroIdentifier(identifier.Name),
        MacroConcatExpression concat        => string.Join(" + ", concat.Expressions.Select(WriteMacroExpression)),
        MacroUnaryExpression unary          => $"({unary.Operator}{WriteMacroExpression(unary.Operand)})",
        MacroBinaryExpression binary when binary.Operator is "<<" or ">>" =>$"({WriteMacroExpression(binary.Left)} {binary.Operator} {WriteShiftOperand(binary.Right)})",
        MacroBinaryExpression binary        => $"({WriteMacroExpression(binary.Left)} {binary.Operator} {WriteMacroExpression(binary.Right)})",
        MacroConditionalExpression ternary  => $"({WriteMacroExpression(ternary.Condition)} ? {WriteMacroExpression(ternary.TrueExpression)} : {WriteMacroExpression(ternary.FalseExpression)})",
        MacroCastExpression cast            => WriteMacroCast(cast),
        MacroCallExpression call            => WriteMacroCall(call),
        MacroMemberExpression member        => $"({WriteMacroExpression(member.Expression)}){(member.IsPointer ? "->" : ".")}{member.Member}",
        _ => throw new NotSupportedException($"Unsupported macro expression: {expression.GetType().Name}")
    };

    string WriteShiftOperand(MacroExpression expression)
    {
        var text = WriteMacroExpression(expression);
        var type = GetMacroExpressionType(null, expression, []);

        if (type?.Def is EnumDef)
            return $"(int){text}";

        return text;
    }

    string WriteMacroCast(MacroCastExpression cast)
    {
        var type = ResolveMacroType(cast.TypeName)
            ?? throw new NotSupportedException($"Unsupported macro cast type '{cast.TypeName}'");

        var nativeType = GetNativeType(type);
        var expression = WriteMacroExpression(cast.Expression);

        if (type.Kind == TypeRefKind.Name && type.Name is
            "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong")
            return $"unchecked(({nativeType})({expression}))";

        return $"(({nativeType})({expression}))";
    }

    string WriteMacroIdentifier(string name)
    {
        if (macroEnumItems.TryGetValue(name, out var mapped))
            return $"{mapped.Enum.Name}.{mapped.Item.Name}";

        foreach (var def in outputDefinitions.OfType<EnumDef>())
        {
            var item = def.Items.FirstOrDefault(x => x.RawName == name);
            if (item != null)
                return $"{def.Name}.{item.Name}";
        }

        return name;
    }

    string WriteMacroCall(MacroCallExpression call)
    {
        if (GetStandardMacroCallType(call.Name) is TypeRef type && call.Arguments.Count == 1)
            return $"(({GetNativeType(type)})({WriteMacroExpression(call.Arguments[0])}))";

        return $"{call.Name}({string.Join(", ", call.Arguments.Select(WriteMacroExpression))})";
    }

    static string WriteMacroLiteral(MacroLiteralExpression literal)
    {
        if (literal.Kind != MacroLiteralKind.Number)
            return literal.Text;

        SplitIntegerLiteral(literal.Text, out var core, out var suffix);
        if (suffix.Length == 0)
        {
            if (core.Length > 1 && core[0] == '0' &&
                !core.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                !core.StartsWith("0b", StringComparison.OrdinalIgnoreCase) &&
                TryParseMacroInteger(core, out var octal))
                return octal.ToString(CultureInfo.InvariantCulture);

            return literal.Text;
        }

        var normalizedSuffix = suffix.ToUpperInvariant() switch
        {
            "LL"       => "L",
            "ULL" or "LLU" => "UL",
            "U"        => "U",
            _          => suffix
        };

        return core + normalizedSuffix;
    }

    static void SplitIntegerLiteral(string text, out string core, out string suffix)
    {
        int index = text.Length;
        while (index > 0 && text[index - 1] is 'u' or 'U' or 'l' or 'L')
            index--;

        core   = text[..index];
        suffix = text[index..];
    }

    static bool TryParseMacroInteger(string text, out ulong value)
    {
        try
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

            if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                value = 0;
                foreach (var c in text.AsSpan(2))
                {
                    if (c is not '0' and not '1')
                        return false;
                    value = checked(value * 2 + (uint)(c - '0'));
                }
                return true;
            }

            if (text.Length > 1 && text[0] == '0')
            {
                value = 0;
                foreach (var c in text.AsSpan(1))
                {
                    if (c < '0' || c > '7')
                        return false;
                    value = checked(value * 8 + (uint)(c - '0'));
                }
                return true;
            }

            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        catch (OverflowException)
        {
            value = 0;
            return false;
        }
    }
}
