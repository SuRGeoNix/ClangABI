namespace ClangABI.Generator;

public unsafe partial class BindingGenerator
{
    void WriteBriefObsolete(BindingDef def, StringBuilder sb, string indent = "")
    {
        //if (def.BriefComment != null)
        //    sb.AppendLine($"{intend}/// <summary>{EscapeXml(def.BriefComment)}</summary>");

        if (!string.IsNullOrWhiteSpace(def.BriefComment))
            WriteXmlDoc(sb, indent, "summary", def.BriefComment);
            //WriteBriefComment(def.BriefComment, sb, indent);

        if (def.Obsolete != null)
            sb.AppendLine($"{indent}[Obsolete(\"{EscapeXml(def.Obsolete)}\")]");
    }

    string WriteFunctionHeader(FunctionLikeDef def, StringBuilder sb, string indent = "\t")
    {
        if (def.BriefComment != null)
            WriteXmlDoc(sb, indent, "summary", def.BriefComment);

        string parameters = "";

        if (def.Parameters != null)
        {
            for (int i = 0; i < def.Parameters.Count; i++)
            {
                var param = def.Parameters[i];

                parameters += $"{GetNativeType(param.Type)} {EscapeIdentifier(param.Name)}";

                if (i != def.Parameters.Count - 1)
                    parameters += ", ";

                if (param.BriefComment != null)
                    WriteXmlDoc(
                        sb,
                        indent,
                        "param",
                        param.BriefComment,
                        $"name=\"{EscapeXml(param.Name)}\"");
            }
        }

        if (def.ReturnComment != null)
            WriteXmlDoc(sb, indent, "returns", def.ReturnComment);

        if (def.Obsolete != null)
            sb.AppendLine($"{indent}[Obsolete(\"{EscapeXml(def.Obsolete)}\")]");

        return parameters;
    }
}
