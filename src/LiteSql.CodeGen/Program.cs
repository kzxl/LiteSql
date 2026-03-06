using System;
using System.IO;

namespace LiteSql.CodeGen
{
    /// <summary>
    /// CLI entry point for LiteSql DBML Code Generator.
    /// 
    /// Usage:
    ///   litesql-codegen &lt;input.dbml&gt; [options]
    /// 
    /// Options:
    ///   -o, --output &lt;path&gt;       Output .cs file path (default: {ContextClass}.designer.cs)
    ///   -n, --namespace &lt;ns&gt;      Target namespace (default: Models)
    /// 
    /// Examples:
    ///   litesql-codegen dbRAF.dbml
    ///   litesql-codegen dbRAF.dbml -o Models/dbRAF.cs -n RAF.Models
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var inputPath = args[0];
            var outputPath = "";
            var targetNamespace = "Models";

            // Parse arguments
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-o":
                    case "--output":
                        if (i + 1 < args.Length) outputPath = args[++i];
                        break;
                    case "-n":
                    case "--namespace":
                        if (i + 1 < args.Length) targetNamespace = args[++i];
                        break;
                }
            }

            // Validate input
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Error: File not found: {inputPath}");
                return 1;
            }

            try
            {
                // Parse DBML
                Console.WriteLine($"Parsing: {inputPath}");
                var model = DbmlParser.Parse(inputPath);
                Console.WriteLine($"  Database: {model.DatabaseName}");
                Console.WriteLine($"  Context:  {model.ContextClassName}");
                Console.WriteLine($"  Tables:   {model.Tables.Count}");

                // Determine output path
                if (string.IsNullOrEmpty(outputPath))
                {
                    var dir = Path.GetDirectoryName(inputPath) ?? ".";
                    outputPath = Path.Combine(dir, $"{model.ContextClassName}.designer.cs");
                }

                // Generate code
                var code = CodeGenerator.Generate(model, targetNamespace);

                // Write output
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                File.WriteAllText(outputPath, code);
                Console.WriteLine($"  Output:   {outputPath}");
                Console.WriteLine();
                Console.WriteLine($"Generated {model.Tables.Count} entity classes + 1 DataContext.");
                Console.WriteLine("Done!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("LiteSql Code Generator - Generate LiteSql-compatible entities from DBML");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  litesql-codegen <input.dbml> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -o, --output <path>       Output .cs file path (default: {ContextClass}.designer.cs)");
            Console.WriteLine("  -n, --namespace <ns>      Target namespace (default: Models)");
            Console.WriteLine("  -h, --help                Show this help");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  litesql-codegen dbRAF.dbml");
            Console.WriteLine("  litesql-codegen dbRAF.dbml -o Models/dbRAF.cs -n RAF.Models");
        }
    }
}
