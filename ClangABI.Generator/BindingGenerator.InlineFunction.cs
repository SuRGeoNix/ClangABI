namespace ClangABI.Generator;

public partial class BindingGenerator
{
    readonly Dictionary<CXCursor, InlineFunctionDef> funcsInlineByCursor = new(new CursorComparer());
    readonly Dictionary<InlineFunctionDef, InlineFunctionSource> inlineFunctionSources = [];

    sealed record InlineFunctionSource(
        string Signature,
        string NativeSignature,
        string Body,
        string Hash);

    void ProcessFunctionInline(CXCursor cursor)
    {
        var definition = cursor.IsDefinition ? cursor : cursor.Definition;
        if (definition.IsNull || !definition.HasBody)
            return;

        var id = cursor.CanonicalCursor;
        
        if (funcsInlineByCursor.TryGetValue(id, out _))
            return;

        // Comes only from top-level (we don't exclude anything here)
        var def = new InlineFunctionDef(
            name:       GetSpelling(cursor),
            returnType: GetType(cursor.ResultType, cursor),
            isVariadic: cursor.Type.IsFunctionTypeVariadic)
        {
            Body        = definition.Body,
            BriefComment= GetBriefComment(cursor),
            ReturnComment=GetReturnComment(cursor),
            Obsolete    = GetObsolete(cursor)
        };

        funcsInlineByCursor.Add(id, def);
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

                def.Parameters.Add(new(
                    paramName,
                    GetParameterType(parameter.Type, parameter)));
            }

            FillParameterComments(cursor, def.Parameters);
        }
    }

    void WriteInlineFunctions()
    {
        var existingByName = existingFuncsInline
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

        StringBuilder sb = new();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine();
        }

        sb.Append(namespaceLine);
        sb.AppendLine("#pragma warning disable IDE1006");
        sb.Append(classNameLine);
        
        foreach (var func in outputDefinitions.OfType<InlineFunctionDef>().OrderBy(x => x.RawName, StringComparer.InvariantCultureIgnoreCase))
        {
            if (func.SkipOutput)
                continue;

            existingByName.TryGetValue(func.RawName, out var existing);
            WriteInlineFunction(func, existing, sb);
        }

        sb.AppendLine("}");

        File.WriteAllText(outputBase + "functions.inline.cs", sb.ToString());
    }

    void WriteInlineFunction(InlineFunctionDef def, ExistingManualBlock? existing, StringBuilder sb)
    {
        var source = GetInlineFunctionSource(def);
        var fresh  = BuildInlineFunctionTemplate(def, source.Signature, source.Body);
        var block  = ResolveManualBlock(existing, source.Hash, fresh);

        if (block.Reset)
            Console.WriteLine($"Manual inline '{def.RawName}' changed in native headers and was reset.");

        WriteManualBlock(
            sb,
            "inline",
            def.RawName,
            source.NativeSignature,
            source.Hash,
            block.Implementation,
            block.Commented);
    }

    InlineFunctionSource GetInlineFunctionSource(InlineFunctionDef def)
    {
        if (inlineFunctionSources.TryGetValue(def, out var source))
            return source;

        var returnType = GetNativeType(def.ReturnType);
        var parameters = def.Parameters == null ? "" :
            string.Join(", ", def.Parameters.Select(x => $"{GetNativeType(x.Type)} {EscapeIdentifier(x.Name)}"));

        var signature = $"public static {returnType} {def.Name}({parameters})";
        var parent    = def.Body.SemanticParent;

        using var policy = parent.PrintingPolicy;
        using var pretty = parent.GetPrettyPrinted(policy);

        var nativeSource = NormalizeInlineSource(pretty.ToString());
        var bodyIndex    = nativeSource.IndexOf('{');

        if (bodyIndex == -1)
            throw new InvalidOperationException($"Body not found for '{def.RawName}'");

        var nativeSignature = string.Join(" ", nativeSource[..bodyIndex]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        source = new(
            signature,
            nativeSignature,
            nativeSource[bodyIndex..],
            GetManualHash(nativeSource));

        inlineFunctionSources.Add(def, source);
        return source;
    }

    string NormalizeInlineSource(string source)
    {
        foreach (var includePath in includePaths.OrderByDescending(x => x.Length))
            source = source.Replace(includePath.Replace('\\', '/'), "", pathComparison);

        return source;
    }

    bool IsInlineFunctionEnabled(InlineFunctionDef def)
    {
        var source   = GetInlineFunctionSource(def);
        var existing = existingFuncsInline.LastOrDefault(x => x.Name == def.RawName);

        if (existing != null)
        {
            if (!existing.IsEnabled || !ManualHashMatches(existing, source.Hash))
                return false;

            foreach (Match declaration in ManualDeclarationRegex.Matches(existing.Implementation))
            {
                if (declaration.Groups["name"].Value != def.RawName || !declaration.Groups["params"].Success)
                    continue;

                var returnType = ResolveManagedMacroType(declaration.Groups["type"].Value);
                var parameters = ParseManualMacroParameters(declaration.Groups["params"].Value);

                if (returnType == null || parameters == null ||
                    !SameMacroType(returnType, def.ReturnType) ||
                    parameters.Count != (def.Parameters?.Count ?? 0))
                    continue;

                bool compatible = true;

                for (int i = 0; i < parameters.Count; i++)
                {
                    if (!SameMacroType(parameters[i].Type, def.Parameters![i].Type))
                    {
                        compatible = false;
                        break;
                    }
                }

                if (compatible)
                    return true;
            }

            return false;
        }

        return false;
    }

    string BuildInlineFunctionTemplate(InlineFunctionDef def, string signature, string body)
    {
        StringBuilder sb = new();

        WriteFunctionHeader(def, sb, "");
        sb.Append(signature);

        if (!body.StartsWith('\r') && !body.StartsWith('\n'))
            sb.AppendLine();

        sb.Append(body);

        return sb.ToString().TrimEnd('\r', '\n');
    }
}
