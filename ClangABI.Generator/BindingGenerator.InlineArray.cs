namespace ClangABI.Generator;

public partial class BindingGenerator
{
    readonly HashSet<long> arrays = [];

    void WriteArrays()
    {
        StringBuilder sb = new();

        if (opts.EmitUsings)
        {
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine();
        }

        sb.Append(namespaceLine);
        
        foreach (var size in arrays.OrderBy(x => x))
            WriteArray(size, sb);

        File.WriteAllText(outputBase + "arrays.g.cs", sb.ToString());
    }

    static void WriteArray(long size, StringBuilder sb) =>
        sb.AppendLine($$"""
            [InlineArray({{size}})]
            public struct Array{{size}}<T> where T : unmanaged
            {
                public readonly int Length => {{size}};
                T _;
            }

            """);
}
