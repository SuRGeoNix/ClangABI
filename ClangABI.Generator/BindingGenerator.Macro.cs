namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    readonly List<MacroDef> macros = [];

    /// <summary>Parsed native macros available for inspection and post-processing.</summary>
    public IReadOnlyList<MacroDef> Macros => macros;

    readonly Dictionary<string, (EnumDef Enum, EnumItemDef Item)> macroEnumItems = new(StringComparer.Ordinal);

    void RegisterManualMacros()
    {
        foreach (var existing in existingMacrosManual
            .Where(x => x.IsEnabled)
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Last()))
        {
            var macro = TryGetMacro(existing.Name);
            if (macro == null || macro.SkipOutput)
                continue;

            var source = GetMacroSource(macro);
            var hash   = GetManualHash(source);

            if (!ManualHashMatches(existing, hash))
            {
                Console.WriteLine($"Manual macro '{macro.RawName}' changed in native headers and was not registered.");
                continue;
            }

            if (!TryGetManualMacroDeclaration(existing, macro, out var returnType, out var parameters))
            {
                Console.WriteLine($"Manual macro '{macro.RawName}' has no active declaration compatible with the native macro and was not registered.");
                continue;
            }

            if (definitionsByRawName.TryGetValue(macro.RawName, out var definitions2) &&
                definitions2.Any(x => x is MacroConstantDef or MacroFunctionDef))
            {
                Console.WriteLine($"Manual macro '{macro.RawName}' conflicts with an existing macro definition and was not registered.");
                continue;
            }

            if (macro.IsFunctionLike)
            {
                var def = new MacroFunctionDef(macro.RawName, returnType, null)
                {
                    SkipOutput     = true,
                    BriefComment   = macro.BriefComment,
                    Obsolete       = macro.Obsolete,
                    Parameters     = parameters
                };

                AddOutputDefinition(def);
            }
            else
            {
                var def = new MacroConstantDef(macro.RawName, returnType, null, false)
                {
                    SkipOutput     = true,
                    BriefComment   = macro.BriefComment,
                    Obsolete       = macro.Obsolete
                };

                AddOutputDefinition(def);
            }
        }
    }

    bool TryGetManualMacroDeclaration(
        ExistingManualBlock existing,
        MacroDef macro,
        out TypeRef returnType,
        out List<ParameterDef>? parameters)
    {
        TypeRef? fallbackReturnType = null;
        List<ParameterDef>? fallbackParameters = null;

        foreach (Match declaration in ManualDeclarationRegex.Matches(existing.Implementation))
        {
            if (declaration.Groups["name"].Value != macro.RawName)
                continue;

            var parametersText = declaration.Groups["params"].Success
                ? declaration.Groups["params"].Value
                : null;

            if ((parametersText != null) != macro.IsFunctionLike)
                continue;

            var candidateReturnType = ResolveManagedMacroType(declaration.Groups["type"].Value);
            if (candidateReturnType == null)
                continue;

            if (!macro.IsFunctionLike)
            {
                returnType = candidateReturnType;
                parameters = null;
                return true;
            }

            var candidateParameters = ParseManualMacroParameters(parametersText!);
            if (candidateParameters == null)
                continue;

            if ((!macro.IsVariadic && candidateParameters.Count != macro.Parameters.Count) ||
                (macro.IsVariadic && candidateParameters.Count < macro.Parameters.Count))
                continue;

            // Preserve the old behavior (arity was sufficient), but prefer the
            // overload whose parameter types match the configured native macro
            // parameter types when multiple convenience overloads are present.
            fallbackReturnType ??= candidateReturnType;
            fallbackParameters ??= candidateParameters;

            bool exact = true;

            for (int i = 0; i < macro.Parameters.Count; i++)
            {
                var nativeType = macro.Parameters[i].Type;
                if (nativeType != null && !SameMacroType(nativeType, candidateParameters[i].Type))
                {
                    exact = false;
                    break;
                }
            }

            if (exact)
            {
                returnType = candidateReturnType;
                parameters = candidateParameters;
                return true;
            }
        }

        if (fallbackReturnType != null)
        {
            returnType = fallbackReturnType;
            parameters = fallbackParameters;
            return true;
        }

        returnType = null!;
        parameters = null;
        return false;
    }

    List<ParameterDef>? ParseManualMacroParameters(string value)
    {
        value = value.Trim();
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
            return null;

        value = value[1..^1].Trim();
        if (value.Length == 0)
            return [];

        var parts      = SplitManagedParameters(value);
        var parameters = new List<ParameterDef>(parts.Count);

        foreach (var part2 in parts)
        {
            var part    = part2.Trim();
            var modifier= ParameterModifier.None;

            if (part.StartsWith("ref ", StringComparison.Ordinal))
            {
                modifier= ParameterModifier.Ref;
                part    = part[4..].TrimStart();
            }
            else if (part.StartsWith("out ", StringComparison.Ordinal))
            {
                modifier= ParameterModifier.Out;
                part    = part[4..].TrimStart();
            }
            else if (part.StartsWith("in ", StringComparison.Ordinal))
                part = part[3..].TrimStart();
            else if (part.StartsWith("params ", StringComparison.Ordinal))
                part = part[7..].TrimStart();

            var split = part.LastIndexOf(' ');
            if (split <= 0 || split == part.Length - 1)
                return null;

            var type = ResolveManagedMacroType(part[..split].Trim());
            if (type == null)
                return null;

            var name = part[(split + 1)..].Trim();
            if (name.StartsWith('@'))
                name = name[1..];

            parameters.Add(new(name, type) { Modifier = modifier });
        }

        return parameters;
    }

    TypeRef? ResolveManagedMacroType(string value)
    {
        value = value.Trim();

        if (value.StartsWith("global::", StringComparison.Ordinal))
            value = value[8..];

        int pointerDepth = 0;
        while (value.EndsWith('*'))
        {
            pointerDepth++;
            value = value[..^1].TrimEnd();
        }

        TypeRef? type = value switch
        {
            "void" or "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or
            "long" or "ulong" or "float" or "double" or "char" or "string" or "object" or
            "nint" or "nuint" or "CLong" or "CULong"
                => TypeRef.FromName(value),

            _ when value.EndsWith("[]", StringComparison.Ordinal)
                => TypeRef.FromName(value),

            _ => TryResolveManagedMacroDefinitionType(value)
        };

        if (type == null)
            return null;

        while (pointerDepth-- > 0)
            type = TypeRef.Pointer(type);

        return type;
    }

    TypeRef? TryResolveManagedMacroDefinitionType(string name)
    {
        if (definitionsByRawName.TryGetValue(name, out var defs))
        {
            var types = defs.Where(x => x is RecordDef or EnumDef or CallbackDef).ToArray();
            if (types.Length == 1)
                return TypeRef.FromDef(types[0]);
        }

        var renamed = outputDefinitions
            .Where(x => x.Name == name && x is RecordDef or EnumDef or CallbackDef)
            .ToArray();

        return renamed.Length == 1 ? TypeRef.FromDef(renamed[0]) : null;
    }

    static List<string> SplitManagedParameters(string value)
    {
        var result = new List<string>();
        int start  = 0;
        int depth  = 0;

        for (int i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;

                case '>':
                case ')':
                case ']':
                    depth--;
                    break;

                case ',' when depth == 0:
                    result.Add(value[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(value[start..]);
        return result;
    }

    string GetMacroSource(MacroDef def)
    {
        var parameters = "";

        if (def.IsFunctionLike)
        {
            var names = def.Parameters.Select(x => x.RawName).ToList();
            if (def.IsVariadic)
                names.Add("...");

            parameters = $"({string.Join(", ", names)})";
        }

        var body = string.Join(" ", def.Tokens.Select(x => x.Text.Replace("\n", "", StringComparison.Ordinal)));
        return $"{def.RawName}{parameters} {body}".TrimEnd();
    }

    void FinalizeMacros()
    {
        foreach (var macro in Macros)
        {
            if (macro.SkipOutput || macro.Expression == null || macro.IsVariadic)
                continue;

            if (definitionsByRawName.ContainsKey(macro.RawName))
                continue;

            var type = GetMacroExpressionType(macro, macro.Expression, new(StringComparer.Ordinal));
            if (type == null)
                continue;

            if (macro.IsFunctionLike)
            {
                if (macro.Parameters.Any(x => x.Type == null))
                    continue;

                CreateMacroFunction(
                    macro,
                    type,
                    macro.Expression,
                    [.. macro.Parameters.Select(x => new ParameterDef(x.RawName, x.Type!))]);
            }
            else
            {
                var isConst = IsMacroConstant(macro, macro.Expression, new(StringComparer.Ordinal)) &&
                              IsCSharpConstType(type);

                CreateMacroConstant(macro, type, macro.Expression, isConst);
            }
        }
    }

    /// <summary>Returns a parsed native macro by its native name.</summary>
    public MacroDef GetMacro(string rawName)
        => Macros.FirstOrDefault(x => x.RawName == rawName)
            ?? throw new KeyNotFoundException($"Macro '{rawName}' not found.");

    /// <summary>Returns a parsed native macro by its native name, or <see langword="null"/> when it was not found.</summary>
    public MacroDef? TryGetMacro(string rawName)
        => Macros.FirstOrDefault(x => x.RawName == rawName);

    /// <summary>Assigns a managed type to a parsed function-like macro parameter.</summary>
    public void SetMacroParameterType(string macroName, string parameterName, TypeRef type)
        => GetMacro(macroName).GetParameter(parameterName).Type = type;

    /// <summary>Creates and registers an enum from the supplied constant macros and suppresses their individual macro output.</summary>
    public EnumDef CreateEnumFromMacros(string? rawName, IEnumerable<MacroDef> macros, bool isFlags = false, TypeRef? type = null)
    {
        var source = macros.Where(x => !x.IsFunctionLike).ToArray();
        if (source.Length == 0)
            //return null;
            throw new InvalidOperationException($"No macros supplied for enum '{rawName}'.");

        if (source.Select(x => x.RawName).Distinct(StringComparer.Ordinal).Count() != source.Length)
            throw new InvalidOperationException($"Duplicate macro names supplied for enum '{rawName}'.");

        if (type != null && !IsEnumUnderlyingType(type))
            throw new InvalidOperationException($"Unsupported enum underlying type '{GetNativeType(type)}' for '{rawName}'.");

        TypeRef? inferredType = null;

        // Validate everything before registering the enum or mutating its source macros.
        foreach (var macro in source)
        {
            if (macroEnumItems.ContainsKey(macro.RawName))
                throw new InvalidOperationException($"Macro '{macro.RawName}' is already mapped to an enum item.");

            if (macro.Expression == null)
                throw new InvalidOperationException($"Macro '{macro.RawName}' could not be parsed and cannot be used by enum '{rawName}'.");

            if (!IsMacroConstant(macro, macro.Expression, new(StringComparer.Ordinal)))
                throw new InvalidOperationException($"Macro '{macro.RawName}' is not a constant expression and cannot be used by enum '{rawName}'.");

            var valueType = GetMacroExpressionType(macro, macro.Expression, new(StringComparer.Ordinal));
            if (valueType == null || !IsEnumUnderlyingType(valueType))
                throw new InvalidOperationException($"Macro '{macro.RawName}' does not resolve to an integer enum value.");

            if (type == null)
            {
                inferredType = inferredType == null
                    ? valueType
                    : GetCommonEnumType(inferredType, valueType);

                if (inferredType == null || !IsEnumUnderlyingType(inferredType))
                    throw new InvalidOperationException($"Unable to determine a common enum type for '{rawName}'.");
            }
        }

        var enumType = type ?? inferredType;

        // C# defaults to int, so don't emit an unnecessary ': int'.
        if (enumType?.Kind == TypeRefKind.Name && enumType.Name == "int")
            enumType = null;

        var items = new List<EnumItemDef>();

        foreach (var macro in source)
        {
            items.Add(new EnumItemDef(macro.RawName, 0)
            {
                BriefComment = macro.BriefComment,
                Obsolete     = macro.Obsolete,
                Expression   = macro.Expression
            });
        }

        var def = CreateEnum(
            rawName,
            isFlags,
            enumType, items);

        for (int i = 0; i < source.Length; i++)
        {
            var macro = source[i];
            var item  = items[i];

            macro.SkipOutput = true;
            macroEnumItems.Add(macro.RawName, (def, item));
        }

        return def;
    }

    /// <summary>Creates and registers an enum from constant macros matching the supplied native-name prefix.</summary>
    public EnumDef CreateEnumFromMacros(string? rawName, string prefix, bool isFlags = false, TypeRef? type = null, string[]? excludePrefixes = null)
        => CreateEnumFromMacros(
            rawName,
            Macros.Where(x => x.RawName.StartsWith(prefix, StringComparison.Ordinal) && !StartsWithAny(x.RawName, excludePrefixes)),
            isFlags,
            type);

    static bool StartsWithAny(string value, string[]? prefixes)
        => prefixes != null && prefixes.Any(x => !string.IsNullOrEmpty(x) && value.StartsWith(x, StringComparison.Ordinal));

    /// <summary>Creates and registers a generated constant from a parsed native macro.</summary>
    public MacroConstantDef CreateMacroConstant(MacroDef macro, TypeRef type, MacroExpression expression, bool isConst = true)
    {
        if (definitionsByRawName.ContainsKey(macro.RawName))
            throw new InvalidOperationException($"Definition '{macro.RawName}' already exists.");

        var def = new MacroConstantDef(macro.RawName, type, expression, isConst)
        {
            BriefComment = macro.BriefComment,
            Obsolete     = macro.Obsolete
        };

        macro.SkipOutput = true;
        AddOutputDefinition(def);
        return def;
    }

    /// <summary>Creates and registers a generated method from a parsed function-like native macro.</summary>
    public MacroFunctionDef CreateMacroFunction(MacroDef macro, TypeRef returnType, MacroExpression expression, params ParameterDef[] parameters)
    {
        if (definitionsByRawName.ContainsKey(macro.RawName))
            throw new InvalidOperationException($"Definition '{macro.RawName}' already exists.");

        var def = new MacroFunctionDef(macro.RawName, returnType, expression)
        {
            BriefComment = macro.BriefComment,
            Obsolete     = macro.Obsolete,
            Parameters   = parameters.Length == 0 ? null : [.. parameters]
        };

        macro.SkipOutput = true;
        AddOutputDefinition(def);
        return def;
    }

    /// <summary>Creates a raw C# macro expression that is emitted without expression rewriting.</summary>
    public static MacroExpression MacroExpressionRaw(string text)
        => new MacroRawExpression(text);
}
