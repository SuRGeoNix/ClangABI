namespace ClangABI.Generator;

/// <summary>Generates low-level C# bindings from Clang-parsed C declarations.</summary>
public unsafe partial class BindingGenerator
{
    readonly List<string>     includePaths        = [];
    readonly HashSet<string>  rootHeaders         = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase: StringComparer.Ordinal);
    readonly StringBuilder    sbRoot              = new();
    readonly BindingOptions   opts;

    ExistingManualBlock[]     existingFuncsInline = [];
    ExistingManualBlock[]     existingMacrosManual= [];
    List<string>              args                = [];
    string                    outputBase          = null!;
    string                    namespaceLine       = null!;
    string                    classNameLine       = null!;
    bool                      generationStarted;

    string nl = Environment.NewLine;

    /// <summary>The mutable options used by this generator. Their current values are validated when <see cref="Generate"/> is called.</summary>
    public BindingOptions Options => opts;

    /// <summary>
    /// Creates a generator associated with the supplied options. No parsing, file-system initialization, or option validation is performed until <see cref="Generate"/>.
    /// </summary>
    public BindingGenerator(BindingOptions options)
        => opts = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Validates the current options, parses the configured native headers, and writes the generated bindings. A generator instance supports one generation run.</summary>
    public void Generate()
    {
        if (generationStarted)
            throw new InvalidOperationException($"This {nameof(BindingGenerator)} instance has already started generation.");

        ValidateOptions();
        InitializeGeneration();
        generationStarted = true;

        using var index         = CXIndex.Create();
        using var unsavedFile   = CXUnsavedFile.Create("_local.h", sbRoot.ToString());
        using var transUnit     = CXTranslationUnit.Parse(
            index,
            "_local.h",
            new([.. args]),
            [unsavedFile],
            CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord | CXTranslationUnit_Flags.CXTranslationUnit_KeepGoing
        );

        // TODO: Diagnostics / Parsing Errors
        for (uint i = 0; i < transUnit.NumDiagnostics; i++)
        {
            using var diagnostic = transUnit.GetDiagnostic(i);

            Console.WriteLine(diagnostic.Format(
                    CXDiagnosticDisplayOptions.CXDiagnostic_DisplaySourceLocation |
                    CXDiagnosticDisplayOptions.CXDiagnostic_DisplayColumn |
                    CXDiagnosticDisplayOptions.CXDiagnostic_DisplayOption));
        }

        transUnit.Cursor.VisitChildren(Process, default);

        ParseMacros();
        opts.PostParse?.Invoke();
        RegisterManualMacros();
        FinalizeMacros();

        WriteMacros();
        WriteManualMacros();
        WriteRecords();
        WriteEnums();
        WriteFunctions();
        WriteInlineFunctions();
        WriteCallbacks();
        WriteArrays();
    }

    void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(opts.OutputDirectory))
            throw new InvalidOperationException($"{nameof(BindingOptions.OutputDirectory)} must be specified.");

        if (string.IsNullOrWhiteSpace(opts.Namespace))
            throw new InvalidOperationException($"{nameof(BindingOptions.Namespace)} must be specified.");

        if (string.IsNullOrWhiteSpace(opts.ClassName))
            throw new InvalidOperationException($"{nameof(BindingOptions.ClassName)} must be specified.");

        if (opts.IncludePaths.Length == 0)
            throw new InvalidOperationException($"{nameof(BindingOptions.IncludePaths)} must contain at least one path.");

        if (opts.IncludeAllHeaders && opts.RootHeaders.Length != 0)
            throw new InvalidOperationException($"Use either {nameof(BindingOptions.IncludeAllHeaders)} or {nameof(BindingOptions.RootHeaders)}, not both.");

        if (!opts.IncludeAllHeaders && opts.RootHeaders.Length == 0)
            throw new InvalidOperationException($"{nameof(BindingOptions.RootHeaders)} must contain at least one header when {nameof(BindingOptions.IncludeAllHeaders)} is disabled.");
    }

    void InitializeGeneration()
    {
        includePaths.Clear();
        rootHeaders.Clear();
        sbRoot.Clear();

        var outPath = Path.GetFullPath(opts.OutputDirectory);

        if (!Path.EndsInDirectorySeparator(outPath))
            outPath += Path.DirectorySeparatorChar;

        outputBase    = outPath + opts.FilePrefix;
        namespaceLine = $"namespace {opts.Namespace};{nl}{nl}";
        classNameLine = $"public unsafe static partial class {opts.ClassName}{nl}{{{nl}";
        args =
            [
            "-x", "c",
            "-std=gnu11",
            "-ferror-limit=0",
            .. opts.AdditionalClangArgs
            ];

        for (int i = 0; i < opts.IncludePaths.Length; i++)
        {
            var path = Path.GetFullPath(opts.IncludePaths[i]);

            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Include path not found: {path}");

            args.Add("-I");
            args.Add(path);

            if (!Path.EndsInDirectorySeparator(path))
                path += Path.DirectorySeparatorChar;

            includePaths.Add(path);
        }

        foreach (var path in opts.QuoteIncludePaths)
        {
            args.Add("-iquote");
            args.Add(path);
        }

        for (int i = 0; i < opts.SystemIncludePaths.Length; i++)
        {
            var path = Path.GetFullPath(opts.SystemIncludePaths[i]);
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"System include path not found: {path}");

            args.Add("-isystem");
            args.Add(path);
        }

        for (int i = 0; i < opts.RootHeaders.Length; i++)
        {
            var header = opts.RootHeaders[i];
            string? fullPath = null;

            if (Path.IsPathRooted(header))
            {
                var path = Path.GetFullPath(header);
                if (File.Exists(path))
                    fullPath = path;
            }
            else
            {
                foreach (var includePath in includePaths)
                {
                    var path = Path.GetFullPath(Path.Combine(includePath, header));

                    if (File.Exists(path))
                    {
                        fullPath = path;
                        break;
                    }
                }
            }

            if (fullPath == null)
                throw new FileNotFoundException($"Root header not found: {header}");

            rootHeaders.Add(fullPath);
            sbRoot.AppendLine($"#include <{(Path.IsPathRooted(header) ? fullPath.Replace('\\', '/') : header)}>");
        }

        if (opts.IncludeAllHeaders)
        {
            List<string> headers = [];
            foreach (var includePath in includePaths)
                headers.AddRange(Directory.GetFiles(includePath, "*.h", SearchOption.AllDirectories));

            rootHeaders.UnionWith(headers);

            foreach (var header in rootHeaders)
                sbRoot.AppendLine($"#include <{header.Replace('\\', '/')}>");
        }

        Directory.CreateDirectory(outPath);

        existingFuncsInline = LoadManualBlocks(outputBase + "functions.inline.cs", "inline");
        existingMacrosManual= LoadManualBlocks(outputBase + "macros.cs", "macro");

        sbRoot.AppendLine(opts.AdditionalSource);
    }

    CXChildVisitResult Process(CXCursor cursor, CXCursor parent, void* clientData)
    {
        if (!IsRootSource(cursor))
            return CXChildVisitResult.CXChildVisit_Continue;

        switch (cursor.Kind)
        {
            case CXCursorKind.CXCursor_MacroDefinition:
                ProcessMacro(cursor);
                break;

            case CXCursorKind.CXCursor_TypedefDecl:
            {
                var type = cursor.TypedefDeclUnderlyingType;
                var decl = type.Declaration;
                var name = GetSpelling(cursor);

                switch (decl.Kind)
                {
                    case CXCursorKind.CXCursor_StructDecl:
                        ProcessStruct(decl, cursor, name);
                        break;

                    case CXCursorKind.CXCursor_UnionDecl:
                        ProcessUnion(decl, cursor, name);
                        break;

                    case CXCursorKind.CXCursor_EnumDecl:
                        ProcessEnum(decl, cursor, name);
                        break;
                }

                if (TryGetCallbackType(type, out var functionType, out _))
                    ProcessCallback(functionType, cursor);

                break;
            }

            case CXCursorKind.CXCursor_StructDecl:
                ProcessStruct(cursor);
                break;

            case CXCursorKind.CXCursor_FunctionDecl:
                if (cursor.Linkage == CXLinkageKind.CXLinkage_Internal || cursor.IsFunctionInlined)
                    ProcessFunctionInline(cursor);
                else
                    ProcessFunction(cursor);
                break;

            case CXCursorKind.CXCursor_EnumDecl:
            case CXCursorKind.CXCursor_FlagEnum:
                ProcessEnum(cursor);
                break;

            case CXCursorKind.CXCursor_UnionDecl:
                ProcessUnion(cursor);
                break;
        }

        return CXChildVisitResult.CXChildVisit_Continue;
    }
}
