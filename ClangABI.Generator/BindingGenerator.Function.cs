namespace ClangABI.Generator;

public partial class BindingGenerator
{
    readonly Dictionary<CXCursor, FunctionDef> funcsByCursor = new(new CursorComparer());

    void ProcessFunction(CXCursor cursor)
    {
        var id = cursor.CanonicalCursor;

        if (funcsByCursor.TryGetValue(id, out _))
            return;

        // TODO: Is it enough that we ignore extra now?
        //if (cursor.Type.IsFunctionTypeVariadic)
            //throw new NotSupportedException("Variadic function not supported");

        // Comes only from top-level (we don't exclude anything here)
        var def = new FunctionDef(
            name:       GetSpelling(cursor),
            returnType: GetType(cursor.ResultType, cursor),
            isVariadic: cursor.Type.IsFunctionTypeVariadic)
        {
            Library     = ResolveLibrary(cursor),
            BriefComment= GetBriefComment(cursor),
            ReturnComment=GetReturnComment(cursor),
            Obsolete    = GetObsolete(cursor)
        };

        funcsByCursor.Add(id, def);
        AddOutputDefinition(def);

        var count = cursor.NumArguments;

        if (count > 0)
        {
            def.Parameters = new(count);
            
            for (uint i = 0; i < count; i++)
            {
                var parameter = cursor.GetArgument(i);
                var paramName = GetSpelling(parameter);
                if (string.IsNullOrEmpty(paramName))
                    paramName = $"p{i}";

                var parameterDef = new ParameterDef(
                    paramName,
                    GetParameterType(parameter.Type, parameter));

                SetParameterCallback(parameterDef);
                def.Parameters.Add(parameterDef);
            }

            FillParameterComments(cursor, def.Parameters);
        }

        opts.PostProcessFunction?.Invoke(def);
    }

    void WriteFunctions()
    {
        StringBuilder sb = new();

        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine("using System.Runtime.InteropServices.Marshalling;");
            sb.AppendLine();
        }

        sb.Append(namespaceLine);
        sb.Append(classNameLine);

        var functions = outputDefinitions.OfType<FunctionDef>()
            .Where(x => !x.SkipOutput)
            .OrderBy(x => x.Library)
            .ThenBy(x => x.RawName)
            .ToArray();

        foreach (var def in functions)
            WriteFunction(def, sb);

        if (functions.Any(HasCStringReturn))
            WriteCStringReturnMarshaller(sb);

        WriteCallbackGeneric(sb);

        sb.AppendLine("}");

        File.WriteAllText(outputBase + "functions.g.cs", sb.ToString());
    }

    void WriteFunction(FunctionDef def, StringBuilder sb)
    {
        var returnType    = GetNativeType(def.ReturnType);
        string parameters = WriteFunctionHeader(def, sb);

        var entryPoint = !def.IsRenamed
            ? ""
            : $", EntryPoint = \"{def.RawName}\"";

        sb.AppendLine($"\t[DllImport({def.Library}{entryPoint}, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]");
        sb.AppendLine($"\tpublic static extern {returnType} {def.Name}({parameters});");
        sb.AppendLine();

        if (NeedsLibraryImportConvenience(def))
            WriteLibraryImportConvenienceFunction(def, sb);
    }

    static bool HasCallbackParameters(FunctionLikeDef def)
        => def.Parameters?.Any(IsCallbackParameter) == true;

    static bool IsCallbackParameter(ParameterDef param)
        => param.Callback != null;

    static bool HasCStringParameters(FunctionLikeDef def)
        => def.Parameters?.Any(IsCStringParameter) == true;

    static bool HasCStringReturn(FunctionLikeDef def)
        => def.ReturnType.Kind == TypeRefKind.CString;

    static bool IsCStringParameter(ParameterDef param)
        => param.Type.Kind == TypeRefKind.CString;

    static bool HasRefParameters(FunctionLikeDef def)
        => def.Parameters?.Any(x => x.Modifier != ParameterModifier.None) == true;

    string GetConvenienceParameter(ParameterDef param)
    {
        if (param.Modifier == ParameterModifier.None)
            return $"{GetNativeType(param.Type)} {EscapeIdentifier(param.Name)}";

        var modifier = param.Modifier == ParameterModifier.Ref ? "ref" : "out";

        return $"{modifier} {GetNativeType(param.Type.ElementType!)} {EscapeIdentifier(param.Name)}";
    }

    static bool NeedsLibraryImportConvenience(FunctionDef def)
        => HasRefParameters(def) ||
           HasCStringParameters(def) ||
           HasCStringReturn(def) ||
           HasCallbackParameters(def);

    string GetLibraryImportParameter(ParameterDef param)
    {
        if (IsCStringParameter(param))
            return $"string {EscapeIdentifier(param.Name)}";

        if (param.Callback is CallbackDef callback)
            return $"{callback.Name} {EscapeIdentifier(param.Name)}";

        return GetConvenienceParameter(param);
    }

    void WriteLibraryImportConvenienceFunction(FunctionDef def, StringBuilder sb)
    {
        _ = WriteFunctionHeader(def, sb);

        bool hasCStringParameters = HasCStringParameters(def);
        bool returnsString        = HasCStringReturn(def);
        bool changesParameters    = hasCStringParameters ||
                                    HasRefParameters(def) ||
                                    HasCallbackParameters(def);

        var returnType = returnsString ? "string?" : GetNativeType(def.ReturnType);
        var signature  = def.Parameters == null
            ? ""
            : string.Join(", ", def.Parameters.Select(GetLibraryImportParameter));

        // C# cannot overload by return type alone. Preserve the exact raw ABI name and
        // suffix only the convenience declaration in this one case.
        var name = changesParameters || !returnsString
            ? def.Name
            : $"{def.Name}_str";

        var stringMarshalling = hasCStringParameters
            ? ", StringMarshalling = StringMarshalling.Utf8"
            : "";

        var entryPoint = name == def.RawName
            ? ""
            : $", EntryPoint = \"{def.RawName}\"";

        sb.AppendLine($"\t[LibraryImport({def.Library}{entryPoint}{stringMarshalling})]");
        sb.AppendLine("\t[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]");

        if (returnsString)
            sb.AppendLine("\t[return: MarshalUsing(typeof(__utf8Marsh))]");

        sb.AppendLine($"\tpublic static partial {returnType} {name}({signature});");
        sb.AppendLine();
    }

    void WriteCStringReturnMarshaller(StringBuilder sb)
    {
        sb.AppendLine("\t[CustomMarshaller(typeof(string), MarshalMode.ManagedToUnmanagedOut, typeof(__utf8Marsh))]");
        sb.AppendLine("\tstatic class __utf8Marsh");
        sb.AppendLine("\t{");
        sb.AppendLine("\t\tpublic static string? ConvertToManaged(byte* value)");
        sb.AppendLine("\t\t\t=> Marshal.PtrToStringUTF8((nint)value);");
        sb.AppendLine("\t}");
        sb.AppendLine();
    }

    void WriteCallbackGeneric(StringBuilder sb)
    {
        sb.AppendLine("\t/// <summary>");
        sb.AppendLine("\t/// Represents a typed native callback pointer for use in unmanaged struct fields.");
        sb.AppendLine("\t/// Allows assigning a managed delegate of type <typeparamref name=\"T\"/> while preserving");
        sb.AppendLine("\t/// the native pointer-sized layout. The delegate must be kept alive by the caller for");
        sb.AppendLine("\t/// as long as native code may invoke the callback.");
        sb.AppendLine("\t/// </summary>");
        sb.AppendLine("\tpublic readonly struct Callback<T>(nint pointer) where T : Delegate");
        sb.AppendLine("\t{");
        sb.AppendLine("\t\tpublic readonly nint Pointer = pointer;");
        sb.AppendLine();
        sb.AppendLine("\t\tpublic static implicit operator Callback<T>(T? callback)");
        sb.AppendLine("\t\t\t=> new(callback is null ? 0 : Marshal.GetFunctionPointerForDelegate(callback));");
        sb.AppendLine("\t}");
        sb.AppendLine();
    }
}
