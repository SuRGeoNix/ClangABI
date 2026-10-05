# ClangABI.Generator

`ClangABI.Generator` is a low-level C-to-C# binding generator built on `ClangSharp.Interop`.

It is designed for projects that need ABI-accurate native bindings while still allowing controlled post-processing, managed naming, selected convenience overloads, macro translation, and manual handling of C constructs that do not map cleanly to C#.

The generator intentionally keeps the raw ABI visible. It does not try to turn a C library into a high-level .NET API.

## Features

- Parses C declarations through Clang.
- Generates structs, unions, enums, functions, callbacks, fixed arrays, bit-fields, and macros.
- Preserves native record size, alignment, and field offsets.
- Keeps non-root dependency records opaque until their layout is actually required.
- Handles portable native-sized types such as `size_t`, `intptr_t`, and `ptrdiff_t` as `nuint` / `nint`.
- Uses `CLong` / `CULong` for platform C `long` / `unsigned long`.
- Generates raw `DllImport` declarations and selected `LibraryImport` convenience overloads.
- Supports UTF-8 C-string parameters and returns.
- Supports typed callback fields through `Callback<T>`.
- Parses and rewrites many object-like and function-like C macros.
- Can create managed enums from groups of integer macros.
- Emits non-constant object-like macros as lazy properties rather than static initializers.
- Preserves manually implemented macros and inline functions across regeneration.
- Detects changes in the corresponding native macro / inline source and resets stale manual implementations.
- Exposes parsed definitions and post-processing hooks for project-specific customization.

## Requirements

The project currently targets **.NET 10** and uses `ClangSharp.Interop`.

The headers required by the native library must be available to Clang through the configured include paths. System or SDK headers can be supplied separately without making them generation roots.

## Basic usage

```csharp
using ClangABI.Generator;
using static ClangABI.Generator.BindingGenerator;

var includePath = Path.GetFullPath("include");
var outputPath  = Path.GetFullPath("Generated");

var options = new BindingOptions
{
    IncludePaths =
    [
        includePath
    ],

    RootHeaders =
    [
        "mylib/mylib.h"
    ],

    OutputDirectory = outputPath,
    Namespace       = "MyLib.ABI",
    ClassName       = "Raw",
    EmitComments    = true,

    // The returned value is emitted as the DllImport library expression.
    ResolveLibrary = _ => "NativeLibrary"
};

var generator = new BindingGenerator(options);

generator.Generate();
```

The generated partial class can provide the library expression used above:

```csharp
namespace MyLib.ABI;

public unsafe static partial class Raw
{
    public const string NativeLibrary = "mylib";
}
```

`BindingOptions` remains mutable until `Generate()` is called. The generator validates and resolves the current option values at that point. A `BindingGenerator` instance is intended for a single generation run.

## Header model

The different include options intentionally have different meanings.

### `IncludePaths`

Normal Clang include paths (`-I`).

They define the native header tree that belongs to the binding input and are also used to determine whether referenced declarations are dependency sources whose layouts may be generated.

```csharp
IncludePaths =
[
    "/sdk/include"
];
```

### `RootHeaders`

The headers whose top-level declarations should be generated.

Root headers are resolved from `IncludePaths`.

```csharp
RootHeaders =
[
    "libfoo/foo.h",
    "libfoo/bar.h"
];
```

Only root declarations are processed initially. Referenced declarations from dependency headers are pulled in when required.

### `IncludeAllHeaders`

Instead of explicitly listing `RootHeaders`, every `.h` file found recursively under `IncludePaths` can be treated as a generation root.

```csharp
IncludeAllHeaders = true;
```

`IncludeAllHeaders` and `RootHeaders` are mutually exclusive.

### `QuoteIncludePaths`

Additional directories searched for quoted includes:

```c
#include "internal.h"
```

They are passed as quote include paths and do **not** become generation or dependency roots.

This is useful when private/native source files expect local sibling headers to be directly searchable.

```csharp
QuoteIncludePaths =
[
    "/sdk/include/libfoo"
];
```

### `SystemIncludePaths`

System include paths (`-isystem`).

Use these for compiler, platform, or SDK headers that Clang must understand but which should remain external to the generated binding model.

```csharp
SystemIncludePaths =
[
    windowsSdkPath,
    compilerIncludePath
];
```

### `AdditionalClangArgs`

Extra arguments passed directly to Clang.

```csharp
AdditionalClangArgs =
[
    "-D__STDC_CONSTANT_MACROS",
    "-D__STDC_FORMAT_MACROS"
];
```

### `AdditionalSource`

Additional C source appended to the synthetic translation unit after the root includes.

This is useful for forcing otherwise pointer-only/private records to require layout, adding prerequisite declarations, or including private implementation source used only for parsing.

```csharp
AdditionalSource = """
    #include <private/internal.h>

    struct __CustomExtra {
        InternalContext context;
    };
    """;
```

Declarations physically written in `AdditionalSource` are treated as root declarations.

## Customizing parsed declarations

The generator exposes its parsed definition model so a project can adjust managed output without changing the native identity.

`RawName` is the native/source identity. `Name` is the managed name emitted in generated code.

```csharp
options.PostProcessFunction = function =>
{
    if (function.RawName == "foo_open")
        function.Name = "Open";
};
```

Available hooks include:

```csharp
options.PostProcessMacro    = macro    => { };
options.PostProcessEnum     = @enum    => { };
options.PreProcessRecord    = record   => { };
options.PostProcessRecord   = record   => { };
options.PostProcessFunction = function => { };
```

`PostProcessMacro` runs after the macro expression has been parsed and before macro finalization, so it can inspect the parsed expression and adjust types or output behavior:

```csharp
options.PostProcessMacro = macro =>
{
    if (!macro.IsFunctionLike &&
        macro.Expression is MacroIdentifierExpression identifier &&
        identifier.Name.EndsWith("_c", StringComparison.Ordinal))
    {
        macro.SkipOutput = true;
    }
};
```

`PostParse` runs after native declarations and macro expressions have been parsed, but before manual macros are registered, macros are finalized, and output is written.

Because the options remain mutable until generation starts, whole-model customization can reference the generator itself:

```csharp
var generator = new BindingGenerator(options);

generator.Options.PostParse = () =>
{
    generator.CreateEnumFromMacros(
        "FooFlags",
        "FOO_FLAG_",
        isFlags: true);
};

generator.Generate();
```

## Definition lookup

Definitions can be inspected or modified during post-processing.

```csharp
var record = generator.GetDefinition<StructDef>("FooContext");
var type   = generator.GetType("FooContext");
```

`GetDefinition(...)` resolves by native/raw name.

`GetDefinitionByName(...)` can be used when the managed output name may have changed; it accepts either the managed name or the raw native name and reports ambiguous matches instead of silently choosing one.

The complete generated declaration set is available through:

```csharp
generator.Definitions
```

and parsed native macros through:

```csharp
generator.Macros
```

## `TypeRef`

`TypeRef` is the generator's Clang-independent type model used by public definitions and post-processing.

Common predefined types include:

```csharp
TypeRef.Int
TypeRef.UInt
TypeRef.Long
TypeRef.ULong
TypeRef.NInt
TypeRef.NUInt
TypeRef.CLong
TypeRef.CULong
TypeRef.CString
TypeRef.String
TypeRef.Object
```

Pointers and fixed arrays can be created explicitly:

```csharp
var pointer = TypeRef.Pointer(TypeRef.Byte);
var array   = TypeRef.Array(TypeRef.Int, 16);
```

A generated declaration can be referenced with:

```csharp
var type = TypeRef.FromDef(definition);
```

## Macros

The generator parses many common C macro expressions and converts them into C# declarations.

### Object-like macros

Values that can be represented as C# compile-time constants are emitted as `const`:

```csharp
public const int FOO_SIZE = 32;
```

Non-constant object-like macros are emitted as **static properties**:

```csharp
public static string LIB_VERSION
    => MAKE_VERSION(LIB_VERSION_MAJOR, LIB_VERSION_MINOR, LIB_VERSION_MICRO);
```

They are deliberately not emitted as `static readonly` fields. Lazy properties avoid initialization-order dependencies between macros and prevent static initialization from accidentally invoking native functions or loading native libraries before the macro is actually used.

### Function-like macros

Function-like macros that can be translated are emitted as static methods:

```csharp
public static int FFALIGN(int x, int a)
    => ((x + a - 1) & ~(a - 1));
```

Parameter types can be supplied when C macro syntax does not provide enough information:

```csharp
generator.SetMacroParameterType("FFALIGN", "x", TypeRef.Int);
generator.SetMacroParameterType("FFALIGN", "a", TypeRef.Int);
```

### Creating enums from macros

Integer macro groups can be converted into managed enums:

```csharp
generator.CreateEnumFromMacros(
    "FooFlags",
    "FOO_FLAG_",
    isFlags: true,
    type: TypeRef.UInt);
```

The source macros are then suppressed from individual output.

### Complex preprocessor constructs

Not every preprocessor construct has a meaningful direct C# equivalent.

Examples include token pasting (`##`), stringification (`#`), some compound literals, and macros whose type depends entirely on their call site.

Those macros can be implemented manually rather than forcing the generic parser to guess at semantics.

## Manual macros and inline functions

Two generated files are intentionally designed for manual implementations:

```text
macros.cs
functions.inline.cs
```

Unsupported macros or inline functions are emitted as commented templates wrapped by metadata markers:

```csharp
/* macro begin: SOME_MACRO | Native: #define SOME_MACRO(x) ... */
// public static TYPE SOME_MACRO(TYPE x)
// {
//     throw new NotImplementedException();
// }
/* macro end: SOME_MACRO | hash: ... */
```

Implement the C# body and uncomment the block:

```csharp
/* macro begin: SOME_MACRO | Native: #define SOME_MACRO(x) ... */
public static int SOME_MACRO(int x)
{
    return ...;
}
/* macro end: SOME_MACRO | hash: ... */
```

On future generation runs:

- an enabled implementation is preserved while the native source hash still matches;
- the generator validates compatible macro / inline signatures where applicable;
- if the native source changes, the manual block is regenerated and commented again so the implementation can be reviewed instead of silently becoming stale.

This allows hand-written translations to live beside generated output without maintaining a separate patching step.

## Functions and managed conveniences

Raw native functions are emitted with `DllImport`.

When the signature supports a useful managed form, the generator can additionally emit `LibraryImport` convenience declarations for:

- UTF-8 C-string parameters;
- UTF-8 C-string returns;
- selected `ref` / `out` pointer parameters;
- callback parameters.

The raw ABI declaration remains available even when a convenience overload is generated.

## Callbacks

Native callback fields use the pointer-sized `Callback<T>` wrapper so the generated record preserves both ABI layout and the expected delegate type:

```csharp
public Callback<MyCallback> callback;
```

A delegate can be assigned directly:

```csharp
MyCallback callback = MyMethod;
native->callback = callback;
```

`Callback<T>` stores only the native function pointer. It does **not** keep the delegate alive. The caller must retain the delegate for as long as native code may invoke it.

Standalone/native callbacks are also emitted as C# delegate definitions with `UnmanagedFunctionPointer(CallingConvention.Cdecl)`.

## Generated files

Depending on the parsed input, generation produces files such as:

```text
arrays.g.cs
callbacks.g.cs
enums.g.cs
functions.g.cs
functions.inline.cs
macros.g.cs
macros.cs
records.g.cs
records.opaque.g.cs
```

`FilePrefix` can prepend a project-specific prefix to every generated file.

For example:

```csharp
FilePrefix = "FFmpeg.";
```

produces:

```text
FFmpeg.functions.g.cs
FFmpeg.records.g.cs
...
```

`EmitUsings` controls whether required `using` directives are emitted per generated file. It should normally remain enabled for reusable output. Projects that provide equivalent global/project usings can disable it.

## Opaque and dependency records

Headers reachable through the configured dependency include tree may contain many records that are never required by value.

The generator keeps external/non-root records opaque when they are referenced only through pointers and only resolves their layout when a generated declaration actually requires it.

Records can also be explicitly intercepted:

```csharp
options.PreProcessRecord = record =>
{
    if (record.RawName == "NativeTypeProvidedElsewhere")
    {
        record.SkipProcess = true;
        record.SkipOutput  = true;
    }
};
```

When `SkipProcess` is used, the caller is responsible for providing any ABI-compatible managed declaration required by generated output.

## Output naming

Native identity and managed naming are intentionally separate:

```csharp
definition.RawName   // native/source identity
definition.Name      // emitted C# name
```

Renaming `Name` does not change the native entry point. Renamed native functions automatically retain their original symbol through `DllImport.EntryPoint`.

This allows idiomatic managed names without losing the original ABI identity.

## Current scope

`ClangABI.Generator` currently focuses on **C ABI generation**.

It is intentionally not:

- a high-level wrapper generator;
- a complete C preprocessor implementation;
- a replacement for project-specific semantic decisions;
- an attempt to hide pointers, layouts, or native ownership rules.

When native C semantics cannot be translated safely and generically, the preferred approach is to expose the parsed model for customization or generate a reviewable manual implementation block.
