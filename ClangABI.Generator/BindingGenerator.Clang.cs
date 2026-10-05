namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    readonly StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static string? GetFilePath(CXCursor cursor)
    {
        CXFile file;
        uint line, column, offset;

        clang.getSpellingLocation(
            cursor.Location,
            (void**)&file,
            &line,
            &column,
            &offset);

        if (file.Handle == 0)
            return null;

        using var name = file.Name;
        return name.ToString();
    }

    string ResolveLibrary(CXCursor cursor)
    {
        var path = GetFilePath(cursor);
        if (path == null)
            throw new NotSupportedException($"Cannot resolve library path");

        if (opts.ResolveLibrary == null)
            return "MANUAL";

        var library = opts.ResolveLibrary.Invoke(path);
        if (string.IsNullOrWhiteSpace(library))
            throw new InvalidOperationException($"Library resolver returned an empty value for '{path}'.");

        return library;
    }

    bool IsRootSource(CXCursor cursor)
    {
        if (cursor.Location.IsFromMainFile)
            return true;

        var path = GetFilePath(cursor);
        if (path == null)
            return false;

        path = Path.GetFullPath(path);

        return rootHeaders.Contains(path);
    }

    bool IsDependencySource(CXCursor cursor)
    {
        if (cursor.Location.IsFromMainFile)
            return true;

        var path = GetFilePath(cursor);
        if (path == null)
            return false;

        path = Path.GetFullPath(path);

        return includePaths.Any(i => path.StartsWith(i, pathComparison));
    }

    static string GetSpelling(CXCursor cursor)
    {
        using var spelling = cursor.Spelling;
        return spelling.ToString();
    }

    static bool IsSignedInteger(CXType type)
    {
        type = clang.getCanonicalType(type);

        if (type.kind == CXTypeKind.CXType_Enum)
            type = clang.getEnumDeclIntegerType(type.Declaration);

        return type.kind switch
        {
            CXTypeKind.CXType_Char_S or
            CXTypeKind.CXType_SChar or
            CXTypeKind.CXType_Short or
            CXTypeKind.CXType_Int or
            CXTypeKind.CXType_Long or
            CXTypeKind.CXType_LongLong => true,

            CXTypeKind.CXType_Bool or
            CXTypeKind.CXType_Char_U or
            CXTypeKind.CXType_UChar or
            CXTypeKind.CXType_UShort or
            CXTypeKind.CXType_UInt or
            CXTypeKind.CXType_ULong or
            CXTypeKind.CXType_ULongLong => false,

            _ => throw new NotSupportedException($"Unsupported bitfield type '{type}' ({type.kind})")
        };
    }

    sealed class CursorComparer : IEqualityComparer<CXCursor>
    {
        public bool Equals(CXCursor x, CXCursor y)
            => clang.equalCursors(x, y) != 0;

        public int GetHashCode(CXCursor cursor)
            => (int)clang.hashCursor(cursor);
    }
}
