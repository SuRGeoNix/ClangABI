namespace ClangABI.Generator;

public partial class BindingGenerator
{
    /// <summary>Base model for a named native binding element exposed for post-processing.</summary>
    public abstract class BindingDef(string rawName)
    {
        /// <summary>Whether the managed output name differs from the native/source name.</summary>
        public bool         IsRenamed       => Name != RawName;

        /// <summary>The native/source identity name. Unnamed declarations receive an identity when <see cref="Name"/> is first assigned.</summary>
        public string       RawName         { get; internal set; } = rawName;

        /// <summary>The managed name emitted in generated source. Defaults to <see cref="RawName"/> and may be changed without renaming an existing native identity.</summary>
        public string       Name            { get => name ?? RawName; set { name = value; if (string.IsNullOrEmpty(RawName)) RawName = value; } }
        string? name;

        /// <summary>Optional obsolete message emitted when <see cref="BindingOptions.EmitObsolete"/> is enabled.</summary>
        public string?      Obsolete        { get; set; }

        /// <summary>Optional brief documentation emitted when <see cref="BindingOptions.EmitComments"/> is enabled.</summary>
        public string?      BriefComment    { get; set; }
    }

    /// <summary>Base model for members of a declaration, such as fields, parameters, and enum items.</summary>
    public abstract class MemberDef(string rawName) : BindingDef(rawName)
    {

    }

    /// <summary>Managed modifier applied by generated convenience signatures to pointer parameters.</summary>
    public enum ParameterModifier
    {
        /// <summary>No managed parameter modifier.</summary>
        None,
        /// <summary>Emits the parameter with the C# <c>ref</c> modifier.</summary>
        Ref,
        /// <summary>Emits the parameter with the C# <c>out</c> modifier.</summary>
        Out,
    }

    /// <summary>Describes a function, callback, or generated macro-function parameter.</summary>
    public sealed class ParameterDef(string rawName, TypeRef type) : MemberDef(rawName)
    {
        /// <summary>Managed/native type emitted for the parameter.</summary>
        public TypeRef              Type        { get; set; } = type;

        /// <summary>Callback definition associated with <see cref="Type"/> when it represents a native function pointer.</summary>
        public CallbackDef?         Callback    => Type.Kind == TypeRefKind.Definition ? Type.Def as CallbackDef : null;

        /// <summary>Optional managed convenience modifier for pointer parameters.</summary>
        public ParameterModifier    Modifier    { get; set; }
    }

    /// <summary>Describes a native record field.</summary>
    public sealed class FieldDef(string rawName, TypeRef type) : MemberDef(rawName)
    {
        /// <summary>Managed/native type emitted for the field.</summary>
        public TypeRef      Type        { get; set; } = type;

        /// <summary>Callback definition associated with <see cref="Type"/> when it represents a native function pointer.</summary>
        public CallbackDef? Callback    => Type.Kind == TypeRefKind.Definition ? Type.Def as CallbackDef : null;

        /// <summary>Bit-field metadata when the native field is a bit-field.</summary>
        public BitFieldDef? BitField    { get; internal set; }

        /// <summary>Native byte offset of the field within its record.</summary>
        public long         Offset      { get; internal set; } = -1;

        /// <summary>Native alignment of the field in bytes.</summary>
        public long         Alignment   { get; internal set; } =  1;
    }

    /// <summary>Describes the native storage and bit range of a bit-field.</summary>
    public sealed class BitFieldDef(int width, int shift, long storageOffset, int storageSize, bool isSigned)
    {
        /// <summary>Bit width of the field.</summary>
        public int          Width        { get; } = width;

        /// <summary>Bit shift within the selected storage unit.</summary>
        public int          Shift        { get; } = shift;

        /// <summary>Byte offset of the selected storage unit within the record.</summary>
        public long         StorageOffset{ get; } = storageOffset;

        /// <summary>Storage unit size in bytes.</summary>
        public int          StorageSize  { get; } = storageSize;

        /// <summary>Whether the native bit-field uses a signed integer type.</summary>
        public bool         IsSigned     { get; } = isSigned;
    }

    /// <summary>Describes a native enum member.</summary>
    public sealed class EnumItemDef(string rawName, long value) : MemberDef(rawName)
    {
        /// <summary>Numeric value of the enum member when no generated expression is used.</summary>
        public long             Value       { get; set; } = value;

        /// <summary>Optional expression emitted instead of <see cref="Value"/>.</summary>
        public MacroExpression? Expression  { get; set; }
    }

    /// <summary>Base model for generated top-level declarations.</summary>
    public abstract class DeclarationDef(string rawName) : BindingDef(rawName)
    {
        /// <summary>Skips writing this declaration while keeping it available for type resolution and post-processing.</summary>
        public bool         SkipOutput  { get; set; }
    }

    /// <summary>Describes a parsed native preprocessor macro before it is finalized for output.</summary>
    public sealed class MacroDef(string name) : BindingDef(name)
    {
        /// <summary>Whether the macro accepts parameters.</summary>
        public bool             IsFunctionLike  { get; internal set; }

        /// <summary>Whether the macro accepts variadic arguments.</summary>
        public bool             IsVariadic      { get; internal set; }

        /// <summary>Skips generated output for this macro.</summary>
        public bool             SkipOutput      { get; set; }

        /// <summary>Parsed macro parameters.</summary>
        public List<MacroParameterDef>
                                Parameters      { get; } = [];

        /// <summary>Tokens captured from the native macro definition.</summary>
        public List<MacroToken> Tokens          { get; } = [];

        /// <summary>Parsed macro expression, or <see langword="null"/> when parsing was not possible.</summary>
        public MacroExpression? Expression      { get; set; }

        /// <summary>Reason expression parsing failed, when available.</summary>
        public string?          ParseError      { get; internal set; }

        /// <summary>Returns the parsed parameter identified by its native name.</summary>
        public MacroParameterDef GetParameter(string rawName)
            => Parameters.FirstOrDefault(x => x.RawName == rawName)
                ?? throw new KeyNotFoundException($"Macro parameter '{RawName}.{rawName}' not found.");
    }

    /// <summary>Describes a native macro parameter.</summary>
    public sealed class MacroParameterDef(string rawName) : MemberDef(rawName)
    {
        /// <summary>Managed type assigned to the macro parameter, when known.</summary>
        public TypeRef? Type { get; set; }
    }

    /// <summary>A token captured from a native macro definition.</summary>
    public sealed record MacroToken(CXTokenKind Kind, string Text);

    /// <summary>Literal category represented by a parsed macro expression.</summary>
    public enum MacroLiteralKind
    {
        /// <summary>Numeric literal.</summary>
        Number,
        /// <summary>Character literal.</summary>
        Character,
        /// <summary>String literal.</summary>
        String,
    }

    /// <summary>Base type for parsed/generated macro expressions.</summary>
    public abstract record MacroExpression;

    /// <summary>Raw C# expression emitted without rewriting.</summary>
    public sealed record MacroRawExpression(string Text) : MacroExpression;

    /// <summary>Literal macro expression.</summary>
    public sealed record MacroLiteralExpression(string Text, MacroLiteralKind Kind) : MacroExpression;

    /// <summary>Identifier macro expression.</summary>
    public sealed record MacroIdentifierExpression(string Name) : MacroExpression;

    /// <summary>Unary operator macro expression.</summary>
    public sealed record MacroUnaryExpression(string Operator, MacroExpression Operand) : MacroExpression;

    /// <summary>Binary operator macro expression.</summary>
    public sealed record MacroBinaryExpression(MacroExpression Left, string Operator, MacroExpression Right) : MacroExpression;

    /// <summary>Conditional (<c>?:</c>) macro expression.</summary>
    public sealed record MacroConditionalExpression(MacroExpression Condition, MacroExpression TrueExpression, MacroExpression FalseExpression) : MacroExpression;

    /// <summary>Cast macro expression.</summary>
    public sealed record MacroCastExpression(string TypeName, MacroExpression Expression) : MacroExpression;

    /// <summary>Function-call macro expression.</summary>
    public sealed record MacroCallExpression(string Name, List<MacroExpression> Arguments) : MacroExpression;

    /// <summary>Concatenated macro expression sequence.</summary>
    public sealed record MacroConcatExpression(List<MacroExpression> Expressions) : MacroExpression;

    /// <summary>Member-access macro expression.</summary>
    public sealed record MacroMemberExpression(MacroExpression Expression, string Member, bool IsPointer) : MacroExpression;

    /// <summary>Generated constant declaration created from a native macro.</summary>
    public sealed class MacroConstantDef(string name, TypeRef type, MacroExpression? expression, bool isConst) : DeclarationDef(name)
    {
        /// <summary>Managed type emitted for the generated constant.</summary>
        public TypeRef           Type       { get; set; } = type;

        /// <summary>Expression emitted as the constant value.</summary>
        public MacroExpression?  Expression { get; set; } = expression;

        /// <summary>Whether the declaration can be emitted as <c>const</c>; otherwise it is emitted as a static property.</summary>
        public bool              IsConst    { get; set; } = isConst;
    }

    /// <summary>Generated method declaration created from a function-like native macro.</summary>
    public sealed class MacroFunctionDef(string name, TypeRef returnType, MacroExpression? expression) : FunctionLikeDef(name, returnType, false)
    {
        /// <summary>Expression emitted as the method body.</summary>
        public MacroExpression? Expression { get; set; } = expression;
    }

    /// <summary>Describes a native or generator-created enum.</summary>
    public sealed class EnumDef(string? name, bool isFlags, TypeRef? type) : DeclarationDef(name ?? string.Empty)
    {
        /// <summary>Explicit underlying type, or <see langword="null"/> for the C# default <see cref="int"/>.</summary>
        public TypeRef?     Type        { get; set; } = type;

        /// <summary>Members emitted for the enum.</summary>
        public List<EnumItemDef>
                            Items       { get; set; } = [];

        /// <summary>Whether the generated enum is emitted with <see cref="FlagsAttribute"/>.</summary>
        public bool         IsFlags     { get; set; } = isFlags;

        /// <summary>Whether the native enum declaration has already been processed.</summary>
        public bool         Processed   { get; internal set; }
    }

    /// <summary>Base model for native struct and union declarations.</summary>
    public abstract class RecordDef(string rawName, bool isExternal) : DeclarationDef(rawName)
    {
        /// <summary>Whether the declaration originates outside the configured root headers and non-system include paths.</summary>
        public bool         IsExternal  { get; internal set; } = isExternal;

        /// <summary>Whether the native declaration has already been processed.</summary>
        public bool         Processed   { get; internal set; }

        /// <summary>Skips field/layout processing. Use for declarations supplied manually by the consumer.</summary>
        public bool         SkipProcess { get; set; }

        /// <summary>Whether the record contains native bit-fields.</summary>
        public bool         HasBitFields{ get; internal set; }

        /// <summary>Native record size in bytes.</summary>
        public long         Size        { get; internal set; }

        /// <summary>Native record alignment in bytes.</summary>
        public long         Alignment   { get; internal set; }

        /// <summary>Processed native fields.</summary>
        public List<FieldDef>
                            Fields      { get; } = [];

        /// <summary>Returns a field identified by its native name.</summary>
        public FieldDef GetField(string rawName)
            => Fields.First(x => x.RawName == rawName);
    }

    /// <summary>Describes a native union declaration.</summary>
    public sealed class UnionDef (string rawName, bool isExternal) : RecordDef(rawName, isExternal);

    /// <summary>Describes a native struct declaration.</summary>
    public sealed class StructDef(string rawName, bool isExternal) : RecordDef(rawName, isExternal);

    /// <summary>Base model for native functions, callbacks, and generated macro functions.</summary>
    public abstract class FunctionLikeDef(string name, TypeRef returnType, bool isVariadic) : DeclarationDef(name)
    {
        /// <summary>Whether the native signature is variadic.</summary>
        public bool     IsVariadic      { get; internal set; } = isVariadic;

        /// <summary>Managed/native return type emitted for the declaration.</summary>
        public TypeRef  ReturnType      { get; set; } = returnType;

        /// <summary>Optional native return-value documentation.</summary>
        public string?  ReturnComment   { get; set; }

        /// <summary>Processed parameters, or <see langword="null"/> when the declaration has no parameters.</summary>
        public List<ParameterDef>?
                        Parameters      { get; set; }

        /// <summary>Returns a parameter identified by its native name.</summary>
        public ParameterDef GetParameter(string rawName)
        {
            if (Parameters != null)
            {
                var parameter = Parameters.FirstOrDefault(x => x.RawName == rawName);
                if (parameter != null)
                    return parameter;
            }

            throw new KeyNotFoundException($"Parameter '{RawName}.{rawName}' not found.");
        }
    }

    /// <summary>Describes a native function-pointer callback signature.</summary>
    public sealed class CallbackDef(string name, TypeRef returnType, bool isVariadic) : FunctionLikeDef(name, returnType, isVariadic)
    {
        /// <summary>Whether the callback originated from a native typedef.</summary>
        public bool IsTypedef { get; internal set; }
    }

    /// <summary>Describes an exported native function.</summary>
    public sealed class FunctionDef(string name, TypeRef returnType, bool isVariadic) : FunctionLikeDef(name, returnType, isVariadic)
    {
        /// <summary>C# expression emitted as the library argument of DllImport/LibraryImport.</summary>
        public string   Library     { get; set; } = null!;
    }

    sealed class InlineFunctionDef(string name, TypeRef returnType, bool isVariadic) : FunctionLikeDef(name, returnType, isVariadic)
    {
        public CXCursor Body        { get; set; }
    }

    void RenameDef(DeclarationDef def, string? publicTypeDefName)
    {
        if (string.IsNullOrEmpty(publicTypeDefName) || publicTypeDefName == def.RawName)
            return;

        Console.WriteLine($"Renamed to {publicTypeDefName} from {def.RawName}");

        if (definitionsByRawName.TryGetValue(def.RawName, out var defs) && defs.Remove(def))
        {
            if (defs.Count == 0)
                definitionsByRawName.Remove(def.RawName);

            def.RawName = publicTypeDefName;
            AddOutputDefinition(def);
        }
        else
            def.RawName = publicTypeDefName;
    }
}
