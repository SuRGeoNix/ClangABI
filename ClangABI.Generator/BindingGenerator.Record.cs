namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    readonly Dictionary<CXCursor, UnionDef> unionsByCursor = new(new CursorComparer());
    readonly Dictionary<CXCursor, StructDef> structsByCursor = new(new CursorComparer());
    
    UnionDef GetOrCreateUnion(CXCursor cursor, CXCursor? context = null, string? publicTypeDefName = null)
    {
        var id = cursor.CanonicalCursor;

        if (unionsByCursor.TryGetValue(id, out var def))
        {
            RenameDef(def, publicTypeDefName);
            return def;
        }
        
        def = new(
            rawName:    GetRecordName(cursor, context, publicTypeDefName),
            isExternal:!IsDependencySource(cursor));
        
        opts.PreProcessRecord?.Invoke(def);

        unionsByCursor.Add(id, def);
        AddOutputDefinition(def);

        return def;
    }

    UnionDef ProcessUnion(CXCursor cursor, CXCursor? context = null, string? publicTypeDefName = null, bool requireLayout = false)
    {
        var def = GetOrCreateUnion(cursor, context, publicTypeDefName);

        if (def.SkipProcess || (def.IsExternal && !requireLayout))
            return def;

        if (!cursor.IsDefinition && requireLayout)
        {
            cursor = cursor.Definition;
            if (cursor.IsNull)
		        throw new InvalidOperationException($"Required layout definition not found for '{def.RawName}'.");
        }

        if (cursor.IsDefinition && !def.Processed)
            ProcessRecordFields(cursor, def);

        return def;
    }

    StructDef GetOrCreateStruct(CXCursor cursor, CXCursor? context = null, string? publicTypeDefName = null)
    {
        var id = cursor.CanonicalCursor;

        if (structsByCursor.TryGetValue(id, out var def))
        {
            RenameDef(def, publicTypeDefName);
            return def;
        }

        def = new(
            rawName:    GetRecordName(cursor, context, publicTypeDefName), 
            isExternal:!IsDependencySource(cursor));
        
        opts.PreProcessRecord?.Invoke(def);

        structsByCursor.Add(id, def);
        AddOutputDefinition(def);
        
        return def;
    }

    StructDef ProcessStruct(CXCursor cursor, CXCursor? context = null, string? publicTypeDefName = null, bool requireLayout = false)
    {
        var def = GetOrCreateStruct(cursor, context, publicTypeDefName);

        // SkipProcess is an explicit user override.
        // The type may still be referenced by generated layouts; the user is responsible for providing an ABI-compatible implementation.
        if (def.SkipProcess || (def.IsExternal && !requireLayout))
            return def;

        if (!cursor.IsDefinition && requireLayout)
        {
            cursor = cursor.Definition;
            if (cursor.IsNull)
		        throw new InvalidOperationException($"Required layout definition not found for '{def.RawName}'.");
        }   

        if (cursor.IsDefinition && !def.Processed)
            ProcessRecordFields(cursor, def);

        return def;
    }

    static string GetRecordName(CXCursor cursor, CXCursor? context, string? publicTypeDefName)
    {
        if (!string.IsNullOrEmpty(publicTypeDefName))
            return publicTypeDefName;

        if (!cursor.IsAnonymous)
            return GetSpelling(cursor);

        if (context is CXCursor c)
        {
            if (c.Kind == CXCursorKind.CXCursor_TypedefDecl)
                return GetSpelling(c);

            if (c.Kind == CXCursorKind.CXCursor_FieldDecl)
                return $"{GetSpelling(cursor.SemanticParent)}_{GetSpelling(c)}";
        }

        throw new NotSupportedException($"Anonymous name not handled");
    }

    void ProcessRecordFields(CXCursor cursor, RecordDef def)
    {
        def.BriefComment= GetBriefComment(cursor);
        def.Obsolete    = GetObsolete(cursor);
        def.Processed   = true;
        def.Size        = cursor.Type.SizeOf;
        def.Alignment   = cursor.Type.AlignOf;

        int structIndex = 0;
        int unionIndex  = 0;

        cursor.VisitChildren((child, parent, data) =>
        {
            switch (child.Kind)
            {
                case CXCursorKind.CXCursor_FieldDecl:
                {
                    var name = GetSpelling(child);

                    if (child.IsBitField)
                    {
                        def.HasBitFields = true;

                        var width = child.FieldDeclBitWidth;
                        if (width < 0)
                            throw new NotSupportedException($"Unable to resolve bitfield width: {def.RawName}.{name}");

                        if (width == 0 || string.IsNullOrEmpty(name))
                            break;

                        var bitOffset = child.OffsetOfField;
                        if (bitOffset < 0)
                            throw new NotSupportedException($"Unable to resolve bitfield offset: {def.RawName}.{name}");

                        var shift = (int)(bitOffset & 7);
                        var bits  = shift + width;
                        var storageSize = bits switch
                        {
                            <= 8  => 1,
                            <= 16 => 2,
                            <= 32 => 4,
                            <= 64 => 8,
                            _ => throw new NotSupportedException($"Bitfield is too wide: {def.RawName}.{name}")
                        };

                        var offset = bitOffset >> 3;
                        def.Fields.Add(new(name, GetType(child.Type, child))
                        {
                            Obsolete     = GetObsolete(child),
                            BriefComment = GetBriefComment(child),
                            Offset       = offset,
                            Alignment    = storageSize,
                            BitField     = new(width, shift, offset, storageSize, IsSignedInteger(child.Type))
                        });

                        break;
                    }

                    var field = new FieldDef(name, GetType(child.Type, child))
                    {
                        Obsolete     = GetObsolete(child),
                        BriefComment = GetBriefComment(child)
                    };

                    var bitOffset2 = child.OffsetOfField;
                    if (bitOffset2 >= 0 && (bitOffset2 & 7) == 0)
                        field.Offset = bitOffset2 >> 3;

                    var alignment = child.Type.AlignOf;
                    if (alignment > 0)
                        field.Alignment = alignment;

                    if (field.Type.Kind == TypeRefKind.Definition &&
                        field.Type.Def is CallbackDef callback)
                    {
                        if (!callback.IsTypedef)
                            callback.Name = ToPascalCase(field.Name) + opts.FieldCallbackSuffix;

                    }

                    def.Fields.Add(field);
                    break;
                }

                case CXCursorKind.CXCursor_StructDecl:
                    if (child.IsAnonymousRecordDecl)
                    {
                        var fieldName = $"struct{structIndex++}";
                        var nested    = ProcessStruct(child, cursor, $"{def.RawName}_{fieldName}", requireLayout: true);

                        def.Fields.Add(new(fieldName, TypeRef.FromDef(nested))
                        {
                            Obsolete     = GetObsolete(child),
                            BriefComment = GetBriefComment(child)
                        });
                    }
                    else if (!child.IsAnonymous && child.IsDefinition)
                        ProcessStruct(child, cursor);

                    break;

                case CXCursorKind.CXCursor_UnionDecl:
                    if (child.IsAnonymousRecordDecl)
                    {
                        var fieldName = $"union{unionIndex++}";
                        var nested    = ProcessUnion(child, cursor, $"{def.RawName}_{fieldName}", requireLayout: true);

                        def.Fields.Add(new(fieldName, TypeRef.FromDef(nested))
                        {
                            Obsolete     = GetObsolete(child),
                            BriefComment = GetBriefComment(child)
                        });
                    }
                    else if (!child.IsAnonymous && child.IsDefinition)
                        ProcessUnion(child, cursor);

                    break;
            }

            return CXChildVisitResult.CXChildVisit_Continue;
        }, default);

        if (def.HasBitFields)
        {
            if (def.Size < 0)
                throw new NotSupportedException($"Unable to resolve record size: {def.RawName}");

            if (def.Alignment <= 0)
                throw new NotSupportedException($"Unable to resolve record alignment: {def.RawName}");

            foreach (var field in def.Fields)
            {
                if (field.Offset < 0)
                    throw new NotSupportedException($"Unable to resolve field offset in bitfield record: {def.RawName}.{field.RawName}");
            }
        }

        opts.PostProcessRecord?.Invoke(def);
    }

    void WriteRecords()
    {
        StringBuilder sb = new();
        StringBuilder sbOpaque = new();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine($"using static {opts.Namespace}.{opts.ClassName};");
            sb.AppendLine();

            sbOpaque.AppendLine("using System;");
            sbOpaque.AppendLine("using System.Runtime.InteropServices;");
            sbOpaque.AppendLine();
        }
        
        sb.Append(namespaceLine);
        sbOpaque.Append(namespaceLine);

        var records = outputDefinitions.OfType<RecordDef>().OrderBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase);
        foreach (var record in records)
        {
            if (record.SkipOutput)
                continue;

            switch (record)
            {
                case StructDef def:
                    WriteRecord(def, record.Processed ? sb : sbOpaque);
                    break;

                case UnionDef def:
                    WriteRecord(def, record.Processed ? sb : sbOpaque, true);
                    break;
            }
        }

        File.WriteAllText(outputBase + "records.g.cs", sb.ToString());
        File.WriteAllText(outputBase + "records.opaque.g.cs", sbOpaque.ToString());
    }

    void WriteRecord(RecordDef def, StringBuilder sb, bool isUnion = false)
    {
        WriteBriefObsolete(def, sb);

        if (def.HasBitFields)
            sb.AppendLine($"[StructLayout(LayoutKind.Explicit, Size = {def.Size})]");
        else if (isUnion)
            sb.AppendLine("[StructLayout(LayoutKind.Explicit)]");

        sb.AppendLine($"public unsafe struct {def.Name}");
        sb.AppendLine("{");

        if (def.HasBitFields)
        {
            var fieldAlignment = def.Fields.Count == 0 ? 1 : def.Fields.Max(x => x.Alignment);
            if (fieldAlignment < def.Alignment)
            {
                var alignmentType = def.Alignment switch
                {
                    2 => "ushort",
                    4 => "uint",
                    8 => "ulong",
                    _ => throw new NotSupportedException($"Unsupported record alignment ({def.Alignment}): {def.RawName}")
                };

                sb.AppendLine("\t[FieldOffset(0)]");
                sb.AppendLine($"\tprivate {alignmentType} __alignment;");

                if (def.Fields.Count != 0)
                    sb.AppendLine();
            }
        }

        for (int i = 0; i < def.Fields.Count; i++)
        {
            if (i != 0)
                sb.AppendLine();

            WriteField(def, def.Fields[i], sb, isUnion);
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    void WriteField(RecordDef record, FieldDef field, StringBuilder sb, bool isUnion)
    {
        if (field.BitField != null)
        {
            WriteBitField(field, sb);
            return;
        }

        var callback = field.Callback;

        if (callback != null && !callback.IsTypedef)
        {
            WriteNestedCallback(callback, sb);
            sb.AppendLine();
        }

        WriteBriefObsolete(field, sb, "\t");

        if (isUnion)
            sb.AppendLine("\t[FieldOffset(0)]");
        else if (record.HasBitFields)
            sb.AppendLine($"\t[FieldOffset({field.Offset})]");

        var type = callback != null
            ? $"Callback<{callback.Name}>"
            : GetNativeType(field.Type);

        sb.AppendLine($"\tpublic {type} {EscapeIdentifier(field.Name)};");
    }

    void WriteBitField(FieldDef field, StringBuilder sb)
    {
        var bit = field.BitField!;
        var storageType = bit.StorageSize switch
        {
            1 => "byte",
            2 => "ushort",
            4 => "uint",
            8 => "ulong",
            _ => throw new NotSupportedException($"Unsupported bitfield storage size: {bit.StorageSize}")
        };

        var storageName = $"__{field.RawName}";

        sb.AppendLine($"\t[FieldOffset({bit.StorageOffset})]");
        sb.AppendLine($"\tprivate {storageType} {storageName};");
        sb.AppendLine();

        WriteBriefObsolete(field, sb, "\t");

        var type        = GetNativeType(field.Type);
        var mask        = bit.Width == 64 ? ulong.MaxValue : (1UL << bit.Width) - 1;
        var shiftedMask = mask << bit.Shift;
        var maskText    = $"0x{mask:X}UL";
        var shiftedMaskText = $"0x{shiftedMask:X}UL";

        string getter;
        if (bit.IsSigned)
        {
            var signShift = 64 - bit.Width;
            getter = $"unchecked(({type})((long)((((ulong){storageName} >> {bit.Shift}) & {maskText}) << {signShift}) >> {signShift}))";
        }
        else
            getter = $"unchecked(({type})(((ulong){storageName} >> {bit.Shift}) & {maskText}))";

        var value = bit.IsSigned
            ? "unchecked((ulong)(long)value)"
            : "(ulong)value";

        sb.AppendLine($"\tpublic {type} {EscapeIdentifier(field.Name)}");
        sb.AppendLine("\t{");
        sb.AppendLine($"\t\treadonly get => {getter};");
        sb.AppendLine($"\t\tset => {storageName} = unchecked(({storageType})(((ulong){storageName} & ~{shiftedMaskText}) | (({value} & {maskText}) << {bit.Shift})));");
        sb.AppendLine("\t}");
    }
}
