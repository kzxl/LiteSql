using System;
using System.IO;
using Microsoft.Data.SqlClient;

namespace LiteSql.CodeGen
{
    /// <summary>
    /// CLI entry point for LiteSql Code Generator.
    /// 
    /// Two modes:
    ///   1. From DBML:  litesql-codegen input.dbml [options]
    ///   2. From DB:    litesql-codegen --connection "Server=...;Database=..." [options]
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

            var outputPath = "";
            var targetNamespace = "Models";
            var connectionString = "";
            var contextName = "";
            var inputPath = "";

            // Detect mode
            if (args[0] == "--connection" || args[0] == "-c")
            {
                if (args.Length < 2) { Console.Error.WriteLine("Error: Missing connection string."); return 1; }
                connectionString = args[1];
                ParseOptions(args, 2, ref outputPath, ref targetNamespace, ref contextName);
            }
            else
            {
                inputPath = args[0];
                ParseOptions(args, 1, ref outputPath, ref targetNamespace, ref contextName);
            }

            try
            {
                DbmlModel model;

                if (!string.IsNullOrEmpty(connectionString))
                {
                    // Mode 2: Read from SQL Server
                    Console.WriteLine("Connecting to database...");
                    using (var conn = new SqlConnection(connectionString))
                    {
                        conn.Open();
                        Console.WriteLine($"  Database: {conn.Database}");
                        model = DatabaseSchemaReader.ReadSchema(conn, 
                            string.IsNullOrEmpty(contextName) ? null : contextName);
                    }
                    Console.WriteLine($"  Context:  {model.ContextClassName}");
                    Console.WriteLine($"  Tables:   {model.Tables.Count}");

                    if (string.IsNullOrEmpty(outputPath))
                        outputPath = $"{model.ContextClassName}.cs";
                }
                else
                {
                    // Mode 1: Parse DBML file
                    if (!File.Exists(inputPath))
                    {
                        Console.Error.WriteLine($"Error: File not found: {inputPath}");
                        return 1;
                    }

                    Console.WriteLine($"Parsing: {inputPath}");
                    model = DbmlParser.Parse(inputPath);
                    Console.WriteLine($"  Database: {model.DatabaseName}");
                    Console.WriteLine($"  Context:  {model.ContextClassName}");
                    Console.WriteLine($"  Tables:   {model.Tables.Count}");

                    if (!string.IsNullOrEmpty(contextName))
                        model.ContextClassName = contextName;

                    if (string.IsNullOrEmpty(outputPath))
                    {
                        var dir = Path.GetDirectoryName(inputPath) ?? ".";
                        var baseName = Path.GetFileNameWithoutExtension(inputPath);
                        outputPath = Path.Combine(dir, $"{baseName}.LiteSql.cs");
                    }
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

                // Warn about L2S conflict (DBML mode only)
                if (!string.IsNullOrEmpty(inputPath))
                {
                    var dir2 = Path.GetDirectoryName(inputPath) ?? ".";
                    var baseName2 = Path.GetFileNameWithoutExtension(inputPath);
                    var designerFile = Path.Combine(dir2, $"{baseName2}.designer.cs");
                    if (File.Exists(designerFile) && outputPath != designerFile)
                    {
                        Console.WriteLine();
                        Console.WriteLine("NOTE: L2S designer file detected:");
                        Console.WriteLine($"  {designerFile}");
                        Console.WriteLine("To avoid class conflicts, either:");
                        Console.WriteLine("  1. Exclude the .designer.cs from your project build");
                        Console.WriteLine("  2. Delete the .designer.cs + .dbml (full migration)");
                        Console.WriteLine("  3. Re-run with -o to overwrite: -o \"" + designerFile + "\"");
                    }
                }

                Console.WriteLine("Done!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        static void ParseOptions(string[] args, int startIdx,
            ref string output, ref string ns, ref string contextName)
        {
            for (int i = startIdx; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-o": case "--output":
                        if (i + 1 < args.Length) output = args[++i]; break;
                    case "-n": case "--namespace":
                        if (i + 1 < args.Length) ns = args[++i]; break;
                    case "--context":
                        if (i + 1 < args.Length) contextName = args[++i]; break;
                }
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("LiteSql Code Generator - Generate LiteSql entities from DBML or SQL Server");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  litesql-codegen <input.dbml> [options]          (from DBML file)");
            Console.WriteLine("  litesql-codegen -c <connection-string> [options] (from SQL Server)");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -o, --output <path>       Output .cs file path");
            Console.WriteLine("  -n, --namespace <ns>      Target namespace (default: Models)");
            Console.WriteLine("  -c, --connection <cs>     SQL Server connection string");
            Console.WriteLine("      --context <name>      Override context class name");
            Console.WriteLine("  -h, --help                Show this help");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  litesql-codegen dbRAF.dbml");
            Console.WriteLine("  litesql-codegen dbRAF.dbml -o Models/dbRAF.cs -n RAF.Models");
            Console.WriteLine("  litesql-codegen -c \"Server=.;Database=RAFInventory;Trusted_Connection=true\" -n RAF.Models");
            Console.WriteLine("  litesql-codegen -c \"Server=.;Database=MyDb;...\" --context MyDataContext -o Models/MyDb.cs");
        }
    }
}
