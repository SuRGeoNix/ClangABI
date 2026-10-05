namespace ClangABI.Generator;

/*
    *_NA        -> real semantic value: Not Applicable / None-ish
    *_NB        -> count/end sentinel, often non-ABI
    *_MAX_ENUM  -> storage-width / compiler sentinel
    *
    * Probably external should be also processed
    * Identify IsFlags based on values?
 */

public unsafe partial class BindingGenerator
{
    readonly Dictionary<CXCursor, EnumDef> enumsByCursor = new(new CursorComparer());
    
    EnumDef GetOrCreateEnum(CXCursor cursor, string? publicTypeDefName = null)
    {
        var id = cursor.CanonicalCursor;

        if (enumsByCursor.TryGetValue(id, out var def))
        {
            RenameDef(def, publicTypeDefName);
            return def;
        }
        
        string? name;

        if (!string.IsNullOrEmpty(publicTypeDefName))
            name = publicTypeDefName;
        else if (cursor.IsAnonymous)
            name = null;
        else
            name = GetSpelling(cursor);

        def = new(name,
            isFlags: cursor.Kind == CXCursorKind.CXCursor_FlagEnum, 
            type:    cursor.EnumDecl_IntegerType.kind != CXTypeKind.CXType_Int ? GetType(cursor.EnumDecl_IntegerType) : null);
        
        enumsByCursor.Add(id, def);

        return def;
    }

    EnumDef ProcessEnum(CXCursor cursor, CXCursor? context = null, string? publicTypeDefName = null)
    {
        var def = GetOrCreateEnum(cursor, publicTypeDefName);

        // TBR: Cannot be excluded! (possible if we convert to int?)
        //if (def.IsExternal)
        //    return def;

        if (def.Processed)
		    return def;

        if (opts.OpaqueEnums.Contains(def.RawName))
	    {
		    def.Type      = GetType(cursor.EnumDecl_IntegerType);
            def.Processed = true;
            FinalizeEnum(def);
		    return def;
	    }

        if (cursor.IsDefinition)
        {
            def.BriefComment= GetBriefComment(cursor);
            def.Obsolete    = GetObsolete(cursor);
            def.Processed   = true;
            
            cursor.VisitChildren((child, parent, data) =>
            {
                if (child.Kind == CXCursorKind.CXCursor_EnumConstantDecl)
                {
                    var name = GetSpelling(child);

                    def.Items.Add(new(name, child.EnumConstantDeclValue)
                    {
                        Obsolete    = GetObsolete(child),
                        BriefComment= GetBriefComment(child)
                    });
                }

                return CXChildVisitResult.CXChildVisit_Continue;
            }, default);

            FinalizeEnum(def);
        }

        return def;
    }

    void FinalizeEnum(EnumDef def, bool postProcess = true)
    {
        if (postProcess)
            opts.PostProcessEnum?.Invoke(def);

        // TBR: Consider assigning a name?
        if (string.IsNullOrEmpty(def.RawName))
            throw new InvalidOperationException("Enum has no name after post-processing.");

        if (definitionsByRawName.ContainsKey(def.RawName))
            throw new InvalidOperationException($"Definition '{def.RawName}' already exists.");

        AddOutputDefinition(def);
    }

    void WriteEnums()
    {
        StringBuilder sb = new();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
        }

        sb.Append(namespaceLine);

        foreach (var enm in outputDefinitions.OfType<EnumDef>().OrderBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase))
            if (!enm.SkipOutput)
                WriteEnum(enm, sb);

        File.WriteAllText(outputBase + "enums.g.cs", sb.ToString());
    }

    void WriteEnum(EnumDef def, StringBuilder sb)
    {
        WriteBriefObsolete(def, sb);

        if (def.IsFlags)
            sb.AppendLine("[Flags]");
        sb.AppendLine($"public enum {def.Name}{(def.Type != null ? " : " + GetNativeType(def.Type!) : "")}");
        sb.AppendLine("{");
        for (int i = 0; i < def.Items.Count; i++)
        {
            var field = def.Items[i];
            WriteBriefObsolete(field, sb, "\t");
            var value = field.Expression != null ? WriteEnumExpression(def, field) : field.Value.ToString();
            sb.AppendLine($"\t{field.Name} = {value}{(i != def.Items.Count - 1 ? "," : "")}");
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    string WriteEnumExpression(EnumDef def, EnumItemDef field)
    {
        var expression = WriteMacroExpression(field.Expression!);

        var enumType = def.Type ?? TypeRef.Int;

        var expressionType = GetMacroExpressionType(
            null,
            field.Expression!,
            new(StringComparer.Ordinal));

        if (expressionType == null ||
            SameMacroType(enumType, expressionType) ||
            CanImplicitlyConvertEnumExpression(expressionType, enumType))
            return expression;

        return $"({GetNativeType(enumType)})({expression})";
    }

    static bool CanImplicitlyConvertEnumExpression(TypeRef from, TypeRef to)
    {
        if (from.Kind != TypeRefKind.Name || to.Kind != TypeRefKind.Name)
            return false;

        return (from.Name, to.Name) switch
        {
            // Normal implicit integral conversions
            ("sbyte", "short" or "int" or "long")                 => true,
            ("byte",  "short" or "ushort" or "int" or "uint" or "long" or "ulong") => true,
            ("short", "int" or "long")                            => true,
            ("ushort","int" or "uint" or "long" or "ulong")       => true,
            ("int",   "long")                                     => true,
            ("uint",  "long" or "ulong")                          => true,
            ("long",  _)                                          => false,

            // Constant-expression conversions:
            // compiler will still reject it if the actual value does not fit.
            ("int", "sbyte" or "byte" or "short" or "ushort" or "uint" or "ulong")
                                                                   => true,

            _ => false
        };
    }

}
