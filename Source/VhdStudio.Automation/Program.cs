using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VhdAttach.Automation;

namespace VhdStudio.Automation {
    internal static class Program {
        internal const long DefaultMaxSize = 100L * 1024 * 1024 * 1024;
        private const int MaxInputCharacters = 64 * 1024;
        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public static async Task<int> Main(string[] args) {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            var mcpRequested = args.Contains("--mcp", StringComparer.Ordinal);
            try {
                var options = Options.Parse(args);
                if (options.Help) {
                    Console.WriteLine(HelpText);
                    return 0;
                }
                if (options.Operation == "capabilities") {
                    Console.WriteLine(JsonSerializer.Serialize(new {
                        success = true,
                        operation = "capabilities",
                        data = new {
                            operations = new[] { "image_list", "image_inspect", "image_create", "image_attach", "image_detach", "operation_status" },
                            formats = new[] { "vhdx" },
                            mcpTransport = "stdio",
                            mutationsRequireAllowWrite = true,
                            defaultMaxSizeBytes = DefaultMaxSize,
                        },
                    }, JsonOptions));
                    return 0;
                }
                if (options.Root == null) { throw new ArgumentException("--root must name an existing local image directory."); }
                using var operations = new AutomationOperations(options.Root, options.MaxSizeBytes, options.AllowWrite);
                if (options.Mcp) {
                    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
                    builder.Logging.ClearProviders();
                    builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
                    builder.Logging.SetMinimumLevel(LogLevel.Warning);
                    builder.Services.AddMcpServer()
                        .WithStdioServerTransport()
                        .WithTools(new DiskTools(operations), JsonOptions);
                    await builder.Build().RunAsync();
                    return 0;
                }
                if (options.Operation == null) { throw new ArgumentException("Choose an operation, --mcp, --help or capabilities."); }
                using var input = options.InputFile != null
                    ? File.OpenText(options.InputFile)
                    : Console.IsInputRedirected ? Console.In : new StringReader("{}");
                using var document = JsonDocument.Parse(ReadBounded(input), new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new ArgumentException("Operation input must be a JSON object."); }
                var result = operations.Execute(options.Operation, document.RootElement);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return result.Success ? 0 : 1;
            } catch (Exception error) {
                var json = JsonSerializer.Serialize(new {
                    success = false,
                    operation = "startup",
                    error = new { code = "invalid_argument", message = error.Message },
                }, JsonOptions);
                // stdout belongs exclusively to the SDK transport while serving MCP.
                (mcpRequested ? Console.Error : Console.Out).WriteLine(json);
                return 2;
            }
        }

        private static string ReadBounded(TextReader input) {
            var buffer = new char[MaxInputCharacters + 1];
            var length = 0;
            while (length < buffer.Length) {
                var read = input.Read(buffer, length, buffer.Length - length);
                if (read == 0) { break; }
                length += read;
            }
            var json = new string(buffer, 0, length);
            if (length > MaxInputCharacters || Encoding.UTF8.GetByteCount(json) > MaxInputCharacters) {
                throw new ArgumentException("JSON input exceeds 64 KiB of UTF-8 text.");
            }
            return string.IsNullOrWhiteSpace(json) ? "{}" : json;
        }

        private sealed class Options {
            internal string Root;
            internal string Operation;
            internal string InputFile;
            internal long MaxSizeBytes = DefaultMaxSize;
            internal bool AllowWrite;
            internal bool Mcp;
            internal bool Help;

            internal static Options Parse(string[] args) {
                var result = new Options();
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < args.Length; i++) {
                    var arg = args[i];
                    if (arg.StartsWith("--", StringComparison.Ordinal) && !seen.Add(arg)) { throw new ArgumentException("Repeated option: " + arg); }
                    switch (arg) {
                        case "--help": case "-h": result.Help = true; break;
                        case "--mcp": result.Mcp = true; break;
                        case "--allow-write": result.AllowWrite = true; break;
                        case "--root": result.Root = Value(args, ref i); break;
                        case "--input": result.InputFile = Value(args, ref i); break;
                        case "--max-size-bytes":
                            if (!long.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out result.MaxSizeBytes)) {
                                throw new ArgumentException("--max-size-bytes must be a positive integer.");
                            }
                            break;
                        default:
                            if (arg.StartsWith("-", StringComparison.Ordinal) || result.Operation != null) { throw new ArgumentException("Unexpected argument: " + arg); }
                            result.Operation = arg;
                            break;
                    }
                }
                if (result.Mcp && (result.Operation != null || result.InputFile != null)) { throw new ArgumentException("--mcp cannot be combined with a CLI operation or --input."); }
                return result;
            }

            private static string Value(string[] args, ref int index) {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) { throw new ArgumentException("Missing option value."); }
                return args[index];
            }
        }

        private const string HelpText = """
            VHD Studio automation: JSON CLI and local MCP server

            VhdStudio.Automation.exe capabilities
            VhdStudio.Automation.exe --root DIRECTORY [--allow-write] [--max-size-bytes N] OPERATION [--input FILE]
            VhdStudio.Automation.exe --root DIRECTORY [--allow-write] [--max-size-bytes N] --mcp

            DIRECTORY must already exist on a local drive. New images must be at least 64 MiB,
            aligned to 4096 bytes. The default maximum capacity is 100 GiB.
            Mutation permissions are fixed at startup; --allow-write authorizes creation,
            read-only/writable attachment and safe detachment within that directory.

            CLI input is one JSON object read from stdin (or --input FILE), limited to 64 KiB.
            Omitting input uses {}. stdout contains one JSON result; exit codes are 0 for success,
            1 for an operation error, and 2 for invalid startup arguments or input.

            Operations and input:
              image_list       {}
              image_inspect    {"path":"example.vhdx"}
              image_create     {"path":"example.vhdx","sizeBytes":1073741824,"prepare":true,"fixedSize":false}
              image_attach     {"path":"example.vhdx","readOnly":true}
              image_detach     {"path":"example.vhdx"}
              operation_status {"requestId":"UUID returned by an earlier mutation"}

            Mutation inputs accept an optional requestId UUID. Reuse it only for the identical
            request. Attach/detach also accept expectedImageId from inspection to reject stale
            selections. Inspect final state after an unknown outcome; never retry with a new ID.

            MCP uses stdio, with protocol data on stdout and diagnostics on stderr. Run it as
            the intended Windows user. Privileged disk work uses the VHD Studio service.
            This release supports VHDX only; VSUX continues to use its interactive password flow.
            """;
    }
}
