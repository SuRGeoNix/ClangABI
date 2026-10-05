namespace ClangABI.Generator;

public partial class BindingGenerator
{
    /// <summary>Describes a managed type used by generated declarations without retaining Clang type state.</summary>
    public sealed class TypeRef
    {
        /// <summary>Managed <c>void</c> type.</summary>
        public static readonly TypeRef Void     = new(TypeRefKind.Name, name: "void");
        /// <summary>Managed <c>bool</c> type.</summary>
        public static readonly TypeRef Bool     = new(TypeRefKind.Name, name: "bool");
        /// <summary>Managed <c>byte</c> type.</summary>
        public static readonly TypeRef Byte     = new(TypeRefKind.Name, name: "byte");
        /// <summary>Managed <c>sbyte</c> type.</summary>
        public static readonly TypeRef SByte    = new(TypeRefKind.Name, name: "sbyte");
        /// <summary>Managed <c>short</c> type.</summary>
        public static readonly TypeRef Short    = new(TypeRefKind.Name, name: "short");
        /// <summary>Managed <c>ushort</c> type.</summary>
        public static readonly TypeRef UShort   = new(TypeRefKind.Name, name: "ushort");
        /// <summary>Managed <c>int</c> type.</summary>
        public static readonly TypeRef Int      = new(TypeRefKind.Name, name: "int");
        /// <summary>Managed <c>uint</c> type.</summary>
        public static readonly TypeRef UInt     = new(TypeRefKind.Name, name: "uint");
        /// <summary>Managed <c>long</c> type.</summary>
        public static readonly TypeRef Long     = new(TypeRefKind.Name, name: "long");
        /// <summary>Managed <c>ulong</c> type.</summary>
        public static readonly TypeRef ULong    = new(TypeRefKind.Name, name: "ulong");
        /// <summary>Managed <c>float</c> type.</summary>
        public static readonly TypeRef Float    = new(TypeRefKind.Name, name: "float");
        /// <summary>Managed <c>double</c> type.</summary>
        public static readonly TypeRef Double   = new(TypeRefKind.Name, name: "double");
        /// <summary>Managed <c>char</c> type.</summary>
        public static readonly TypeRef Char     = new(TypeRefKind.Name, name: "char");
        /// <summary>Managed native signed integer type.</summary>
        public static readonly TypeRef NInt     = new(TypeRefKind.Name, name: "nint");
        /// <summary>Managed native unsigned integer type.</summary>
        public static readonly TypeRef NUInt    = new(TypeRefKind.Name, name: "nuint");
        /// <summary>Platform C <c>long</c> represented by <see cref="System.Runtime.InteropServices.CLong"/>.</summary>
        public static readonly TypeRef CLong    = new(TypeRefKind.Name, name: "CLong");
        /// <summary>Platform C <c>unsigned long</c> represented by <see cref="System.Runtime.InteropServices.CULong"/>.</summary>
        public static readonly TypeRef CULong   = new(TypeRefKind.Name, name: "CULong");
        /// <summary>Null-terminated native C string represented as <c>byte*</c> in raw bindings.</summary>
        public static readonly TypeRef CString  = new(TypeRefKind.CString);
        /// <summary>Managed <c>string</c> type.</summary>
        public static readonly TypeRef String   = new(TypeRefKind.Name, name: "string");
        /// <summary>Managed <c>object</c> type.</summary>
        public static readonly TypeRef Object   = new(TypeRefKind.Name, name: "object");

        /// <summary>Kind of type reference.</summary>
        public TypeRefKind      Kind        { get; }

        /// <summary>Managed type name when <see cref="Kind"/> is <see cref="TypeRefKind.Name"/>.</summary>
        public string?          Name        { get; }

        /// <summary>Referenced declaration when <see cref="Kind"/> is <see cref="TypeRefKind.Definition"/>.</summary>
        public DeclarationDef?  Def         { get; }

        /// <summary>Pointed-to or array element type for pointer/array references.</summary>
        public TypeRef?         ElementType { get; }

        /// <summary>Fixed element count when <see cref="Kind"/> is <see cref="TypeRefKind.Array"/>.</summary>
        public long             ArraySize   { get; }

        TypeRef(TypeRefKind kind, string? name = null, DeclarationDef? def = null, TypeRef? elementType = null, long arraySize = 0)
        {
            Kind        = kind;
            Name        = name;
            Def         = def;
            ElementType = elementType;
            ArraySize   = arraySize;
        }

        /// <summary>Creates a type reference to a generated declaration.</summary>
        public static TypeRef FromDef(DeclarationDef def)
            => new(TypeRefKind.Definition, def: def);

        /// <summary>Creates a pointer type reference.</summary>
        public static TypeRef Pointer(TypeRef elementType)
            => new(TypeRefKind.Pointer, elementType: elementType);

        /// <summary>Creates a fixed-size array type reference.</summary>
        public static TypeRef Array(TypeRef elementType, long size)
            => new(TypeRefKind.Array, elementType: elementType, arraySize: size);

        /// <summary>Returns a known primitive type reference or creates a named managed type reference.</summary>
        public static TypeRef FromName(string name) => name switch
        {
            "void"      => Void,
            "bool"      => Bool,
            "byte"      => Byte,
            "sbyte"     => SByte,
            "short"     => Short,
            "ushort"    => UShort,
            "int"       => Int,
            "uint"      => UInt,
            "long"      => Long,
            "ulong"     => ULong,
            "float"     => Float,
            "double"    => Double,
            "char"      => Char,
            "string"    => String,
            "nint"      => NInt,
            "nuint"     => NUInt,
            "CLong"     => CLong,
            "CULong"    => CULong,
            "object"    => Object,
            _           => new(TypeRefKind.Name, name: name)
        };
    }

    /// <summary>Supported forms of generated type references.</summary>
    public enum TypeRefKind
    {
        /// <summary>Named managed type.</summary>
        Name,
        /// <summary>Reference to a generated declaration.</summary>
        Definition,
        /// <summary>Native pointer type.</summary>
        Pointer,
        /// <summary>Fixed-size native array type.</summary>
        Array,
        /// <summary>Null-terminated native C string.</summary>
        CString,
    }

    enum RecordTypeRequirement
    {   // Keep non-root records opaque when only referenced through pointers
        Reference,
        Layout,
    }

    /// <summary>Returns a generated type by its native/source name.</summary>
    public TypeRef GetType(string rawName)
    {
        if (!definitionsByRawName.TryGetValue(rawName, out var defs))
            throw new KeyNotFoundException($"Definition '{rawName}' not found.");

        var types = defs.Where(x => x is RecordDef or EnumDef or CallbackDef).ToArray();

        return types.Length switch
        {
            1 => TypeRef.FromDef(types[0]),
            0 => throw new InvalidCastException($"Definition '{rawName}' is not a type."),
            _ => throw new InvalidOperationException($"Type definition '{rawName}' is ambiguous.")
        };
    }

    readonly Dictionary<string, List<DeclarationDef>> definitionsByRawName = new(StringComparer.Ordinal);
    readonly List<DeclarationDef> outputDefinitions = [];

    /// <summary>Declarations registered for generated output and post-processing.</summary>
    public IReadOnlyList<DeclarationDef> Definitions => outputDefinitions;

    void RegisterDefinition(DeclarationDef def)
    {
        if (!definitionsByRawName.TryGetValue(def.RawName, out var defs))
            definitionsByRawName.Add(def.RawName, defs = []);

        if (!defs.Contains(def))
            defs.Add(def);
    }

    void AddOutputDefinition(DeclarationDef def)
    {
        if (!outputDefinitions.Contains(def))
            outputDefinitions.Add(def);

        RegisterDefinition(def);
    }

    /// <summary>Creates and registers a generated enum, optionally invoking the configured enum post-processor.</summary>
    public EnumDef CreateEnum(string? rawName, bool isFlags = false, TypeRef? type = null, IEnumerable<EnumItemDef>? items = null, bool postProcess = true)
    {
        var def = new EnumDef(rawName, isFlags, type);
        
        if (items != null)
            def.Items.AddRange(items);

        FinalizeEnum(def, postProcess);

        return def;
    }

    /// <summary>Returns the single declaration registered with the specified native/source name.</summary>
    public DeclarationDef GetDefinition(string rawName)
    {
        if (!definitionsByRawName.TryGetValue(rawName, out var defs))
            throw new KeyNotFoundException($"Definition '{rawName}' not found.");

        return defs.Count switch
        {
            1 => defs[0],
            _ => throw new InvalidOperationException($"Definition '{rawName}' is ambiguous.")
        };
    }

    /// <summary>Returns the single declaration of <typeparamref name="T"/> registered with the specified native/source name.</summary>
    public T GetDefinition<T>(string rawName)
        where T : DeclarationDef
    {
        if (!definitionsByRawName.TryGetValue(rawName, out var defs))
            throw new KeyNotFoundException($"Definition '{rawName}' not found.");

        var matches = defs.OfType<T>().ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidCastException(
                $"Definition '{rawName}' is '{string.Join(", ", defs.Select(x => x.GetType().Name))}', expected '{typeof(T).Name}'."),
            _ => throw new InvalidOperationException($"Definition '{rawName}' is ambiguous for '{typeof(T).Name}'.")
        };
    }

    /// <summary>
    /// Gets a declaration by either its managed output name or its native raw name.
    /// Throws if no matching declaration exists or if the name is ambiguous.
    /// </summary>
    public DeclarationDef GetDefinitionByName(string name)
    {
        var matches = definitionsByRawName.Values
            .SelectMany(x => x)
            .Distinct()
            .Where(x => x.RawName == name || (x.Name != null && x.Name == name))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"Definition with managed name '{name}' not found."),
            _ => throw new InvalidOperationException($"Definition with managed name '{name}' is ambiguous.")
        };
    }

    /// <summary>
    /// Gets a declaration of type <typeparamref name="T"/> by either its managed output name
    /// or its native raw name. Throws if no matching declaration exists or if the name is ambiguous.
    /// </summary>
    public T GetDefinitionByName<T>(string name)
        where T : DeclarationDef
    {
        var defs = definitionsByRawName.Values
            .SelectMany(x => x)
            .Distinct()
            .Where(x => x.RawName == name || (x.Name != null && x.Name == name))
            .ToArray();

        if (defs.Length == 0)
            throw new KeyNotFoundException($"Definition with managed name '{name}' not found.");

        var matches = defs.OfType<T>().ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidCastException(
                $"Definition with managed name '{name}' is '{string.Join(", ", defs.Select(x => x.GetType().Name))}', expected '{typeof(T).Name}'."),
            _ => throw new InvalidOperationException($"Definition with managed name '{name}' is ambiguous for '{typeof(T).Name}'.")
        };
    }

    TypeRef GetType(CXType type, CXCursor? context = null, RecordTypeRequirement requirement = RecordTypeRequirement.Layout) 
    {
        if (GetKnownTypedef(type) is { } known)
		    return known;

        return type.kind switch
	    {
            CXTypeKind.CXType_Void      => TypeRef.Void,
            CXTypeKind.CXType_Bool      => TypeRef.Byte, // TBR: bool 4-byte vs 1-byte in C

            CXTypeKind.CXType_Char_S    => TypeRef.Byte,
            CXTypeKind.CXType_Char_U    => TypeRef.Byte,
            CXTypeKind.CXType_SChar     => TypeRef.SByte,
            CXTypeKind.CXType_UChar     => TypeRef.Byte,

            CXTypeKind.CXType_Short     => TypeRef.Short,
            CXTypeKind.CXType_UShort    => TypeRef.UShort,
            CXTypeKind.CXType_Int       => TypeRef.Int,
            CXTypeKind.CXType_UInt      => TypeRef.UInt,

            CXTypeKind.CXType_Long      => TypeRef.CLong,
            CXTypeKind.CXType_ULong     => TypeRef.CULong,

            CXTypeKind.CXType_LongLong  => TypeRef.Long,
            CXTypeKind.CXType_ULongLong => TypeRef.ULong,

            CXTypeKind.CXType_Float     => TypeRef.Float,
            CXTypeKind.CXType_Double    => TypeRef.Double,
            CXTypeKind.CXType_Elaborated=> GetType(type.NamedType, context, requirement),

            _ => GetComplexType(type, context, requirement)
        };
    }
    
    TypeRef GetComplexType(CXType type, CXCursor? context = null, RecordTypeRequirement requirement = RecordTypeRequirement.Layout)
    {
        switch (type.kind)
        {
            case CXTypeKind.CXType_Pointer:
                var pointee = UnwrapElaborated(type.PointeeType);
                var requirement2 = RecordTypeRequirement.Reference;

                if (pointee.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto)
                    return GetType(pointee, context, requirement2);

                if ((pointee.kind is CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U) &&
                    clang.isConstQualifiedType(pointee) != 0)
                    return TypeRef.CString;

                if (context is CXCursor c &&
                    c.Kind == CXCursorKind.CXCursor_FieldDecl)
                {
                    var decl = pointee.Declaration;

                    if ((decl.Kind is CXCursorKind.CXCursor_StructDecl or CXCursorKind.CXCursor_UnionDecl) &&
                        decl.IsAnonymous &&
                        decl.IsDefinition &&
                        decl.SemanticParent == c.SemanticParent)
                        requirement2 = RecordTypeRequirement.Layout;
                }

                return TypeRef.Pointer(GetType(pointee, context, requirement2));


            case CXTypeKind.CXType_ConstantArray:
                var size    = type.ArraySize;
                var element = type.ArrayElementType;

                arrays.Add(size);

                return TypeRef.Array(GetType(element, context, requirement), size);

            case CXTypeKind.CXType_Typedef:
                return ProcessTypedef(type.Declaration, requirement);

            case CXTypeKind.CXType_Enum:
                return TypeRef.FromDef(ProcessEnum(type.Declaration, context));

            case CXTypeKind.CXType_Record:
                {
                    var decl = type.Declaration;

                    return decl.Kind switch
                    {
                        CXCursorKind.CXCursor_StructDecl => TypeRef.FromDef(requirement == RecordTypeRequirement.Layout
                            ? ProcessStruct(decl, context, requireLayout: true)
                            : GetOrCreateStruct(decl, context)),

                        CXCursorKind.CXCursor_UnionDecl => TypeRef.FromDef(requirement == RecordTypeRequirement.Layout
                            ? ProcessUnion(decl, context, requireLayout: true)
                            : GetOrCreateUnion(decl, context)),

                        _ => TypeRef.FromName(type.ToString())
                    };
                }

            case CXTypeKind.CXType_FunctionProto:
            case CXTypeKind.CXType_FunctionNoProto:
                if (context == null)
                    throw new NotSupportedException("[Callback] Missing context");

                return TypeRef.FromDef(ProcessCallback(type, context.Value));

            default:
                throw new NotSupportedException($"Unsupported type '{type}' ({type.kind})");
        }
    }

    // Preserve well-known fixed-width/platform typedef semantics before Clang resolves them
    // to their target-specific underlying C types (e.g. int64_t -> long on Linux x64).
    // Without this, Linux generation could incorrectly emit CLong/CULong instead of
    // long/ulong/nint/nuint, producing target-dependent bindings.
    static TypeRef? GetKnownTypedef(CXType type)
    {
	    using var cxName = clang.getTypedefName(type);

	    return cxName.ToString() switch
	    {
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
            //"ssize_t"                   => TypeRef.NInt,
            //"intmax_t"                  => TypeRef.Long,
            //"uintmax_t"                 => TypeRef.ULong,

		    _ => null
	    };
    }

    string GetNativeType(TypeRef type)
    {
        switch (type.Kind)
        {
            case TypeRefKind.Name:
                return type.Name!;

            case TypeRefKind.Definition:
                if (type.Def is CallbackDef callback)
                    return GetNativeType(callback);

                return type.Def!.Name
                    ?? throw new InvalidOperationException("Definition name has not been resolved");

            case TypeRefKind.Pointer:
                return $"{GetNativeType(type.ElementType!)}*";

            case TypeRefKind.CString:
                return "byte*";

            case TypeRefKind.Array:
            {
                var element = type.ElementType!;
                var elementType = IsPointerType(element)
                    ? "nint"
                    : GetNativeType(element);

                return $"Array{type.ArraySize}<{elementType}>";
            }

            default:
                throw new NotSupportedException(type.Kind.ToString());
        }
    }

    TypeRef GetParameterType(CXType type, CXCursor? context = null)
    {
        type = UnwrapElaborated(type);

        var parameterType = type;

        while (parameterType.kind == CXTypeKind.CXType_Typedef)
        {
            var declaration = parameterType.Declaration;
            parameterType = clang.getTypedefDeclUnderlyingType(declaration);
            parameterType = UnwrapElaborated(parameterType);
        }

        if (parameterType.kind is CXTypeKind.CXType_ConstantArray or CXTypeKind.CXType_IncompleteArray)
            return TypeRef.Pointer(GetType(parameterType.ArrayElementType, context, RecordTypeRequirement.Reference));

        return GetType(type, context);
    }

    TypeRef ProcessTypedef(CXCursor cursor, RecordTypeRequirement requirement = RecordTypeRequirement.Layout)
    {
        var type = cursor.TypedefDeclUnderlyingType;

        if (TryGetCallbackType(type, out var functionType, out _))
            return TypeRef.FromDef(ProcessCallback(functionType, cursor));

        var name = GetSpelling(cursor);

        switch (name)
        {
            case "intptr_t":
            case "ptrdiff_t":
                return TypeRef.NInt;

            case "uintptr_t":
            case "size_t":
                return TypeRef.NUInt;
        }

        var underlying = UnwrapElaborated(type);
        var decl       = underlying.Declaration;

        return decl.Kind switch
        {
            CXCursorKind.CXCursor_StructDecl => TypeRef.FromDef(requirement == RecordTypeRequirement.Layout
                ? ProcessStruct(decl, cursor, name, requireLayout: true)
                : GetOrCreateStruct(decl, cursor, name)),

            CXCursorKind.CXCursor_UnionDecl => TypeRef.FromDef(requirement == RecordTypeRequirement.Layout
                ? ProcessUnion(decl, cursor, name, requireLayout: true)
                : GetOrCreateUnion(decl, cursor, name)),

            CXCursorKind.CXCursor_EnumDecl => TypeRef.FromDef(ProcessEnum(decl, cursor, name)),

            _ => GetType(type, cursor, requirement),
        };
    }

    static CXType UnwrapElaborated(CXType type)
    {
        while (type.kind == CXTypeKind.CXType_Elaborated)
            type = type.NamedType;

        return type;
    }

    static bool TryGetCallbackType(CXType type, out CXType functionType, out bool pointerInTypedef)
    {
        type = UnwrapElaborated(type);

        if (type.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto)
        {
            functionType     = type;
            pointerInTypedef = false;

            return true;
        }

        if (type.kind == CXTypeKind.CXType_Pointer)
        {
            var pointee = UnwrapElaborated(type.PointeeType);

            if (pointee.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto)
            {
                functionType     = pointee;
                pointerInTypedef = true;

                return true;
            }
        }

        if (type.kind == CXTypeKind.CXType_Typedef)
            return TryGetCallbackType(type.Declaration.TypedefDeclUnderlyingType, out functionType, out pointerInTypedef);

        functionType     = default;
        pointerInTypedef = false;

        return false;
    }

    static bool IsPointerType(TypeRef type) => 
        type.Kind is TypeRefKind.Pointer or TypeRefKind.CString ||
        type.Kind == TypeRefKind.Definition && type.Def is CallbackDef;
}
