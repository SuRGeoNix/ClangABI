namespace ClangABI.Generator;

/// <summary>
/// Configures a <see cref="BindingGenerator"/> generation run.
/// Options remain mutable until <see cref="BindingGenerator.Generate"/> is called and should not be modified while generation is in progress.
/// </summary>
public class BindingOptions
{
    /// <summary>Header search paths passed to Clang with <c>-I</c>. Root headers are resolved against these paths.</summary>
    public string[]             IncludePaths            { get; set; } = [];

    /// <summary>Additional directories searched for quoted includes (<c>#include "..."</c>) without treating them as generation or dependency roots.</summary>
    public string[]             QuoteIncludePaths       { get; set; } = [];

    /// <summary>System header search paths passed to Clang with <c>-isystem</c>.</summary>
    public string[]             SystemIncludePaths      { get; set; } = [];

    /// <summary>Headers treated as generation roots. Relative paths are resolved against <see cref="IncludePaths"/>; absolute paths are also accepted. Mutually exclusive with <see cref="IncludeAllHeaders"/>.</summary>
    public string[]             RootHeaders             { get; set; } = [];

    /// <summary>Includes every header found recursively under <see cref="IncludePaths"/> as a generation root.</summary>
    public bool                 IncludeAllHeaders       { get; set; }

    /// <summary>Directory where generated source files are written.</summary>
    public string               OutputDirectory         { get; set; } = string.Empty;

    /// <summary>Namespace emitted for generated declarations.</summary>
    public string               Namespace               { get; set; } = string.Empty;

    /// <summary>Name of the static class containing generated functions and macros.</summary>
    public string               ClassName               { get; set; } = "Raw";

    /// <summary>Prefix added to every generated output file name.</summary>
    public string               FilePrefix              { get; set; } = string.Empty;

    /// <summary>Additional command-line arguments passed directly to Clang.</summary>
    public string[]             AdditionalClangArgs     { get; set; } = [];

    /// <summary>Additional C source appended to the synthetic translation unit after the root header includes.</summary>
    public string               AdditionalSource        { get; set; } = string.Empty;

    /// <summary>Emits the required using directives in each generated source file. Disable only when equivalent global usings are supplied by the consuming project.</summary>
    public bool                 EmitUsings              { get; set; } = true;

    /// <summary>Emits available native documentation comments.</summary>
    public bool                 EmitComments            { get; set; }

    /// <summary>Emits obsolete metadata discovered in the native declarations.</summary>
    public bool                 EmitObsolete            { get; set; }

    /// <summary>Native enum names whose members should not be emitted while preserving the enum and its underlying type.</summary>
    public HashSet<string>      OpaqueEnums             { get; set; } = [];

    /// <summary>Suffix applied to generated delegate names for callbacks declared directly as record fields.</summary>
    public string               FieldCallbackSuffix     { get; set; } = "Callback";

    /// <summary>
    /// Resolves the source header path of a native function to the C# library expression emitted in its DllImport attribute.
    /// When unset, <c>MANUAL</c> is emitted as the library expression.
    /// </summary>
    public Func<string, string>?ResolveLibrary          { get; set; }

    /// <summary>Invoked after a macro has been parsed and before it is finalized for output.</summary>
    public Action<MacroDef>?    PostProcessMacro        { get; set; }

    /// <summary>Invoked after an enum has been parsed and before it is finalized for output.</summary>
    public Action<EnumDef>?     PostProcessEnum         { get; set; }

    /// <summary>Invoked after a record is identified and before its fields and native layout are processed.</summary>
    public Action<RecordDef>?   PreProcessRecord        { get; set; }

    /// <summary>Invoked after a record and its fields/native layout have been processed.</summary>
    public Action<RecordDef>?   PostProcessRecord       { get; set; }

    /// <summary>Invoked after a native function and its parameters have been processed.</summary>
    public Action<FunctionDef>? PostProcessFunction     { get; set; }

    /// <summary>
    /// Invoked after native declarations and macro expressions have been parsed, before manual macros are registered,
    /// macros are finalized, and output files are written.
    /// </summary>
    public Action?              PostParse               { get; set; }
}
