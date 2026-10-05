namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    TypeRef? GetMacroExpressionType(MacroDef? context, MacroExpression expression, HashSet<string> stack)
    {
        switch (expression)
        {
            case MacroRawExpression:
                return null;

            case MacroLiteralExpression literal:
                return GetMacroLiteralType(literal);

            case MacroConcatExpression concat:
            {
                foreach (var item in concat.Expressions)
                {
                    var type = GetMacroExpressionType(context, item, stack);

                    if (type?.Kind != TypeRefKind.Name || type.Name != "string")
                        return null;
                }

                return TypeRef.String;
            }

            case MacroIdentifierExpression identifier:
            {
                var parameter = context?.Parameters.FirstOrDefault(x => x.RawName == identifier.Name);
                if (parameter != null)
                    return parameter.Type;

                if (macroEnumItems.TryGetValue(identifier.Name, out var enumItem))
                    return TypeRef.FromDef(enumItem.Enum);

                var enumType = FindEnumItemType(identifier.Name);
                if (enumType != null)
                    return TypeRef.FromDef(enumType);

                if (definitionsByRawName.TryGetValue(identifier.Name, out var definitions2))
                {
                    var constant = definitions2.OfType<MacroConstantDef>().FirstOrDefault();
                    if (constant != null)
                        return constant.Type;
                }

                var macro = TryGetMacro(identifier.Name);
                if (macro?.Expression == null || macro.IsFunctionLike || !stack.Add(macro.RawName))
                    return null;

                try
                {
                    return GetMacroExpressionType(macro, macro.Expression, stack);
                }
                finally
                {
                    stack.Remove(macro.RawName);
                }
            }

            case MacroUnaryExpression unary:
            {
                var type = GetMacroExpressionType(context, unary.Operand, stack);
                if (type == null)
                    return null;

                return unary.Operator switch
                {
                    "!" => TypeRef.Bool,
                    "+" or "~" => PromoteMacroInteger(type),
                    "-" => GetSignedMacroType(PromoteMacroInteger(type)),
                    _ => null
                };
            }

            case MacroBinaryExpression binary:
            {
                var left  = GetMacroExpressionType(context, binary.Left, stack);
                var right = GetMacroExpressionType(context, binary.Right, stack);
                if (left == null || right == null)
                    return null;

                return binary.Operator switch
                {
                    "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||" => TypeRef.Bool,
                    "<<" or ">>" => PromoteMacroInteger(left),
                    "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" => GetCommonMacroType(left, right),
                    _ => null
                };
            }

            case MacroConditionalExpression conditional:
            {
                var trueType  = GetMacroExpressionType(context, conditional.TrueExpression, stack);
                var falseType = GetMacroExpressionType(context, conditional.FalseExpression, stack);

                return trueType == null || falseType == null
                    ? null
                    : GetCommonMacroType(trueType, falseType);
            }

            case MacroCastExpression cast:
                return ResolveMacroType(cast.TypeName);

            case MacroCallExpression call:
            {
                var standardType = GetStandardMacroCallType(call.Name);
                if (standardType != null)
                    return standardType;

                definitionsByRawName.TryGetValue(call.Name, out var defs);

                var manual = defs?.OfType<MacroFunctionDef>().FirstOrDefault(x => x.Expression == null);
                if (manual != null)
                    return manual.ReturnType;

                var macro = TryGetMacro(call.Name);
                if (macro?.Expression != null && macro.IsFunctionLike && !stack.Contains(macro.RawName))
                {
                    if (macro.Parameters.Any(x => x.Type == null))
                        return null;

                    stack.Add(macro.RawName);
                    try
                    {
                        return GetMacroExpressionType(macro, macro.Expression, stack);
                    }
                    finally
                    {
                        stack.Remove(macro.RawName);
                    }
                }

                if (defs != null)
                {
                    var function = defs.OfType<MacroFunctionDef>().FirstOrDefault()
                        ?? defs.OfType<FunctionLikeDef>()
                            .FirstOrDefault(x => x is not InlineFunctionDef inline || IsInlineFunctionEnabled(inline));

                    if (function != null)
                        return function.ReturnType;
                }

                return null;
            }

            default:
                return null;
        }
    }

    bool IsMacroConstant(MacroDef context, MacroExpression expression, HashSet<string> stack)
    {
        switch (expression)
        {
            case MacroRawExpression:
                return false;

            case MacroLiteralExpression:
                return true;

            case MacroConcatExpression concat:
                return concat.Expressions.All(x => IsMacroConstant(context, x, stack));

            case MacroIdentifierExpression identifier:
            {
                if (context.Parameters.Any(x => x.RawName == identifier.Name))
                    return false;

                if (macroEnumItems.ContainsKey(identifier.Name) || FindEnumItemType(identifier.Name) != null)
                    return true;

                if (definitionsByRawName.TryGetValue(identifier.Name, out var definitions2))
                {
                    var constant = definitions2.OfType<MacroConstantDef>().FirstOrDefault();
                    if (constant != null)
                        return constant.IsConst;
                }

                var macro = TryGetMacro(identifier.Name);
                if (macro?.Expression == null || macro.IsFunctionLike || !stack.Add(macro.RawName))
                    return false;

                try
                {
                    return IsMacroConstant(macro, macro.Expression, stack);
                }
                finally
                {
                    stack.Remove(macro.RawName);
                }
            }

            case MacroUnaryExpression unary:
                return IsMacroConstant(context, unary.Operand, stack);

            case MacroBinaryExpression binary:
                return IsMacroConstant(context, binary.Left, stack) &&
                       IsMacroConstant(context, binary.Right, stack);

            case MacroConditionalExpression conditional:
                return IsMacroConstant(context, conditional.Condition, stack) &&
                       IsMacroConstant(context, conditional.TrueExpression, stack) &&
                       IsMacroConstant(context, conditional.FalseExpression, stack);

            case MacroCastExpression cast:
                return ResolveMacroType(cast.TypeName) != null && IsMacroConstant(context, cast.Expression, stack);

            case MacroCallExpression call:
                return IsStandardConstantMacroCall(call.Name) && call.Arguments.All(x => IsMacroConstant(context, x, stack));

            default:
                return false;
        }
    }

    EnumDef? FindEnumItemType(string rawName)
    {
        foreach (var def in outputDefinitions.OfType<EnumDef>())
            if (def.Items.Any(x => x.RawName == rawName))
                return def;

        return null;
    }

    internal TypeRef? ResolveMacroType(string value)
    {
        var type = string.Join(" ", value
            .Replace("*", " * ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x is not "const" and not "volatile"));

        int pointerDepth = 0;
        while (type.EndsWith(" *", StringComparison.Ordinal))
        {
            pointerDepth++;
            type = type[..^2].TrimEnd();
        }

        TypeRef? result = type switch
        {
            "void"                      => TypeRef.Void,
            "char"                      => TypeRef.Byte,
            "signed char"               => TypeRef.SByte,
            "unsigned char"             => TypeRef.Byte,

            "short" or "short int" or "signed short" or "signed short int"
                                        => TypeRef.Short,
            "unsigned short" or "unsigned short int"
                                        => TypeRef.UShort,

            "int" or "signed" or "signed int"
                                        => TypeRef.Int,
            "unsigned" or "unsigned int"
                                        => TypeRef.UInt,

            "long long" or "long long int" or "signed long long" or "signed long long int"
                                        => TypeRef.Long,
            "unsigned long long" or "unsigned long long int"
                                        => TypeRef.ULong,

            "float"                     => TypeRef.Float,
            "double"                    => TypeRef.Double,

            "int8_t"                    => TypeRef.SByte,
            "uint8_t"                   => TypeRef.Byte,
            "int16_t"                   => TypeRef.Short,
            "uint16_t"                  => TypeRef.UShort,
            "int32_t"                   => TypeRef.Int,
            "uint32_t"                  => TypeRef.UInt,
            "int64_t"                   => TypeRef.Long,
            "uint64_t"                  => TypeRef.ULong,

            "intptr_t" or "ptrdiff_t"   => TypeRef.NInt,
            "uintptr_t" or "size_t"     => TypeRef.NUInt,

            _                           => TryResolveMacroDefinitionType(type)
        };

        if (result == null)
            return null;

        while (pointerDepth-- > 0)
            result = TypeRef.Pointer(result);

        return result;
    }

    TypeRef? TryResolveMacroDefinitionType(string name)
    {
        if (!definitionsByRawName.TryGetValue(name, out var defs))
            return null;

        var types = defs.Where(x => x is RecordDef or EnumDef or CallbackDef).ToArray();
        return types.Length == 1 ? TypeRef.FromDef(types[0]) : null;
    }

    TypeRef? GetMacroLiteralType(MacroLiteralExpression literal)
    {
        return literal.Kind switch
        {
            MacroLiteralKind.String    => literal.Text.StartsWith('"') ? TypeRef.String : null,
            MacroLiteralKind.Character => literal.Text.StartsWith('\'') ? TypeRef.Int : null,
            MacroLiteralKind.Number    => GetMacroNumberType(literal.Text),
            _ => null
        };
    }

    static TypeRef? GetMacroNumberType(string text)
    {
        if (text.Contains('.') || text.Contains('e') || text.Contains('E') || text.Contains('p') || text.Contains('P'))
        {
            if (text.Contains('p') || text.Contains('P') || text.EndsWith('l') || text.EndsWith('L'))
                return null; // C hexadecimal / long-double literals have no direct C# representation here.

            return text.EndsWith('f') || text.EndsWith('F')
                ? TypeRef.Float
                : TypeRef.Double;
        }

        SplitIntegerLiteral(text, out var core, out var suffix);
        var upperSuffix = suffix.ToUpperInvariant();

        if (upperSuffix is "L" or "UL" or "LU")
            return null; // C long is platform dependent.

        if (upperSuffix is "LL")
            return TypeRef.Long;

        if (upperSuffix is "ULL" or "LLU")
            return TypeRef.ULong;

        if (!TryParseMacroInteger(core, out var value))
            return null;

        if (upperSuffix == "U")
            return value <= uint.MaxValue ? TypeRef.UInt : TypeRef.ULong;

        if (upperSuffix.Length != 0)
            return null;

        if (core.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            core.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ||
            core.Length > 1 && core[0] == '0')
        {
            if (value <= int.MaxValue)  return TypeRef.Int;
            if (value <= uint.MaxValue) return TypeRef.UInt;
            if (value <= long.MaxValue) return TypeRef.Long;
            return TypeRef.ULong;
        }

        if (value <= int.MaxValue)  return TypeRef.Int;
        if (value <= long.MaxValue) return TypeRef.Long;
        return TypeRef.ULong;
    }

    static bool IsCSharpConstType(TypeRef type)
    {
        if (type.Kind == TypeRefKind.Definition)
            return type.Def is EnumDef;

        if (type.Kind != TypeRefKind.Name)
            return false;

        return type.Name is
            "bool" or
            "byte" or "sbyte" or
            "short" or "ushort" or
            "int" or "uint" or
            "long" or "ulong" or
            "char" or
            "float" or "double" or
            "string";
    }

    static bool IsEnumUnderlyingType(TypeRef type)
        => type.Kind == TypeRefKind.Name && type.Name is
            "byte" or "sbyte" or
            "short" or "ushort" or
            "int" or "uint" or
            "long" or "ulong";

    static TypeRef? GetCommonEnumType(TypeRef left, TypeRef right)
    {
        if (SameMacroType(left, right))
            return left;

        if (!IsEnumUnderlyingType(left) || !IsEnumUnderlyingType(right))
            return null;

        var leftUnsigned  = left.Name is "byte" or "ushort" or "uint" or "ulong";
        var rightUnsigned = right.Name is "byte" or "ushort" or "uint" or "ulong";
        if (leftUnsigned != rightUnsigned)
            return null;

        return GetMacroNumericRank(left.Name!) >= GetMacroNumericRank(right.Name!)
            ? left
            : right;
    }

    static TypeRef? GetStandardMacroCallType(string name) => name switch
    {
        // <stdint.h> *_C macros use the promoted least-width integer type.
        // On the targets supported by this generator, 8/16-bit constants
        // promote to int rather than retaining byte/short width.
        "INT8_C" or "UINT8_C" or
        "INT16_C" or "UINT16_C" or
        "INT32_C"  => TypeRef.Int,
        "UINT32_C" => TypeRef.UInt,
        "INT64_C"  => TypeRef.Long,
        "UINT64_C" => TypeRef.ULong,
        _ => null
    };

    static bool IsStandardConstantMacroCall(string name)
        => GetStandardMacroCallType(name) != null;

    static TypeRef PromoteMacroInteger(TypeRef type)
    {
        if (type.Kind != TypeRefKind.Name)
            return type;

        return type.Name switch
        {
            "byte" or "sbyte" or "short" or "ushort" or "char" => TypeRef.Int,
            _ => type
        };
    }

    static TypeRef GetSignedMacroType(TypeRef type)
    {
        if (type.Kind != TypeRefKind.Name)
            return type;

        return type.Name switch
        {
            "uint"  => TypeRef.Int,
            "ulong" => TypeRef.Long,
            _ => type
        };
    }

    static TypeRef? GetCommonMacroType(TypeRef left, TypeRef right)
    {
        if (SameMacroType(left, right))
            return left;

        if (left.Kind == TypeRefKind.Definition || right.Kind == TypeRefKind.Definition)
            return null;

        left  = PromoteMacroInteger(left);
        right = PromoteMacroInteger(right);

        if (SameMacroType(left, right))
            return left;

        if (left.Kind != TypeRefKind.Name || right.Kind != TypeRefKind.Name)
            return null;

        var leftRank  = GetMacroNumericRank(left.Name!);
        var rightRank = GetMacroNumericRank(right.Name!);
        if (leftRank < 0 || rightRank < 0)
            return null;

        return leftRank >= rightRank ? left : right;
    }

    static bool SameMacroType(TypeRef left, TypeRef right)
    {
        if (left.Kind != right.Kind)
            return false;

        return left.Kind switch
        {
            TypeRefKind.Name       => left.Name == right.Name,
            TypeRefKind.Definition => ReferenceEquals(left.Def, right.Def),
            TypeRefKind.Pointer    => left.ElementType != null && right.ElementType != null && SameMacroType(left.ElementType, right.ElementType),
            TypeRefKind.CString    => true,
            _ => false
        };
    }

    static int GetMacroNumericRank(string type) => type switch
    {
        "bool"   => 0,
        "byte"   => 1,
        "sbyte"  => 1,
        "short"  => 2,
        "ushort" => 2,
        "char"   => 2,
        "int"    => 3,
        "uint"   => 4,
        "long"   => 5,
        "ulong"  => 6,
        "float"  => 7,
        "double" => 8,
        _ => -1
    };
}
