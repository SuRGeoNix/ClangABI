namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    readonly Dictionary<CXCursor, CallbackDef> callbacksByCursor = new(new CursorComparer());
    readonly List<CallbackDef> globalCallbacks = [];

    CallbackDef ProcessCallback(CXType type, CXCursor context)
    {
        bool isTypedef = context.Kind == CXCursorKind.CXCursor_TypedefDecl &&
                         TryGetCallbackType(context.TypedefDeclUnderlyingType, out var contextType, out _) &&
                         contextType == type;

        var id = context.CanonicalCursor;

        if (callbacksByCursor.TryGetValue(id, out var def))
            return def;

        def = new CallbackDef(
            name:       isTypedef ? GetSpelling(context) : $"{GetSpelling(context.SemanticParent)}_{GetSpelling(context)}",
            returnType: GetType(type.ResultType, context),
            isVariadic: type.IsFunctionTypeVariadic)
        {
            IsTypedef = isTypedef
        };

        callbacksByCursor.Add(id, def);

        if (isTypedef)
        {
            def.BriefComment = GetBriefComment(context);
            def.ReturnComment= GetReturnComment(context);
            def.Obsolete     = GetObsolete(context);
            AddOutputDefinition(def);
        }
        else
            RegisterDefinition(def);
        
        if (def.IsVariadic)
            throw new NotSupportedException($"Variadic callback not supported: {def.RawName}");

        int count = type.NumArgTypes;
        if (count > 0)
        {
            def.Parameters = new(count);

            var parameters = new List<CXCursor>(count);

            context.VisitChildren((child, parent, data) =>
            {
                if (child.Kind == CXCursorKind.CXCursor_ParmDecl)
                    parameters.Add(child);

                return CXChildVisitResult.CXChildVisit_Continue;
            }, default);

            for (int i = 0; i < count; i++)
            {
                CXCursor? parameter = i < parameters.Count ? parameters[i] : null;

                var rawName = parameter != null ? GetSpelling(parameter.Value) : null;
                
                if (string.IsNullOrEmpty(rawName))
                    rawName = $"p{i}";

                def.Parameters.Add(new(
                    rawName,
                    GetParameterType(type.GetArgType((uint)i), parameter ?? context)));
            }

            FillParameterComments(context, def.Parameters);
        }

        return def;
    }

    void SetParameterCallback(ParameterDef parameter)
    {
        if (parameter.Callback is not CallbackDef callback)
            return;

        if (!callback.IsTypedef && !globalCallbacks.Contains(callback))
            globalCallbacks.Add(callback);
    }

    string GetNativeType(CallbackDef def)
    {
        var parameters = def.Parameters == null ? "" : string.Join(", ", def.Parameters.Select(x => GetNativeType(x.Type)));

        return
            $"delegate* unmanaged[Cdecl]<" +
            $"{parameters}{(parameters.Length > 0 ? ", " : "")}" +
            $"{GetNativeType(def.ReturnType)}>";
    }

    void WriteNestedCallback(CallbackDef def, StringBuilder sb)
    {
        var parameters = def.Parameters == null
            ? ""
            : string.Join(", ", def.Parameters.Select(x =>
                $"{GetNativeType(x.Type)}{(!string.IsNullOrEmpty(x.Name) ? " " + EscapeIdentifier(x.Name) : "")}"));

        sb.AppendLine("\t[UnmanagedFunctionPointer(CallingConvention.Cdecl)]");
        sb.AppendLine($"\tpublic delegate {GetNativeType(def.ReturnType)} {def.Name}({parameters});");
    }

    void WriteCallbacks()
    {
        StringBuilder sb = new();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
        }

        sb.Append(namespaceLine);

        var callbacks = outputDefinitions
            .OfType<CallbackDef>()
            .Concat(globalCallbacks)
            .Distinct()
            .OrderBy(x => x.RawName, StringComparer.InvariantCultureIgnoreCase);

        foreach (var callback in callbacks)
            WriteCallback(callback, sb);

        File.WriteAllText(outputBase + "callbacks.g.cs", sb.ToString());
    }

    void WriteCallback(CallbackDef def, StringBuilder sb)
    {
        WriteBriefObsolete(def, sb);

        var parameters = def.Parameters == null
            ? ""
            : string.Join(", ", def.Parameters.Select(x =>
                $"{GetNativeType(x.Type)}{(!string.IsNullOrEmpty(x.Name) ? " " + EscapeIdentifier(x.Name) : "")}"));

        sb.AppendLine("[UnmanagedFunctionPointer(CallingConvention.Cdecl)]");
        sb.AppendLine($"public unsafe delegate {GetNativeType(def.ReturnType)} {def.Name}({parameters});");
        sb.AppendLine();
    }
}
