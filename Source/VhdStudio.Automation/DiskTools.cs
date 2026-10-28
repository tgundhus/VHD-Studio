using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VhdAttach.Automation;

namespace VhdStudio.Automation {
    [McpServerToolType]
    internal sealed class DiskTools {
        private readonly AutomationOperations Operations;
        private static readonly JsonSerializerOptions ArgumentOptions = new JsonSerializerOptions(Program.JsonOptions) {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        internal DiskTools(AutomationOperations operations) { Operations = operations; }

        [McpServerTool(Name = "image_list", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationResult))]
        [Description("List VHDX images directly inside the configured image directory. Returns identity and attachment information without modifying disks.")]
        public CallToolResult List() => Run("image_list", new { });

        [McpServerTool(Name = "image_inspect", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationResult))]
        [Description("Inspect a VHDX image within the configured directory. Returns its stable identity, capacity, attachment state and available volume paths.")]
        public CallToolResult Inspect([Description("Image path within the configured directory.")] string path)
            => Run("image_inspect", new { path });

        [McpServerTool(Name = "image_create", Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationResult))]
        [Description("Create a new VHDX within the authorized directory and capacity limit. Existing files are refused. Preparation creates GPT and one NTFS partition only on this new image, then safely detaches it. Requires --allow-write.")]
        public CallToolResult Create(
            [Description("New .vhdx path within the configured directory.")] string path,
            [Description("Virtual capacity in bytes; at least 64 MiB for blank and prepared images, aligned to 4096 bytes, within the configured limit.")] long sizeBytes,
            [Description("Prepare GPT and one NTFS partition, leaving the result detached.")] bool prepare = true,
            [Description("Allocate the full image capacity immediately instead of using a dynamic image.")] bool fixedSize = false,
            [Description("Optional unique UUID for durable request tracking and safe retry reconciliation.")] string requestId = null)
            => Run("image_create", new { path, sizeBytes, prepare, fixedSize, requestId });

        [McpServerTool(Name = "image_attach", Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationResult))]
        [Description("Attach a VHDX through the secured Windows service and return actual volume paths. Defaults to read-only. Requires --allow-write even for read-only attachment. Existing images are never initialized or formatted.")]
        public CallToolResult Attach(
            [Description("Image path within the configured directory.")] string path,
            [Description("Mount with a read-only payload; false permits writing files.")] bool readOnly = true,
            [Description("Optional image identity returned by inspection; rejects a changed image.")] string expectedImageId = null,
            [Description("Optional unique UUID for durable request tracking and safe retry reconciliation.")] string requestId = null)
            => Run("image_attach", new { path, readOnly, expectedImageId, requestId });

        [McpServerTool(Name = "image_detach", Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationResult))]
        [Description("Safely detach a VHDX through the secured service after flushing, locking and dismounting its volumes. Busy volumes are refused; there is no forced detach. Requires --allow-write.")]
        public CallToolResult Detach(
            [Description("Image path within the configured directory.")] string path,
            [Description("Optional image identity returned by inspection; rejects a changed image.")] string expectedImageId = null,
            [Description("Optional unique UUID for durable request tracking and safe retry reconciliation.")] string requestId = null)
            => Run("image_detach", new { path, expectedImageId, requestId });

        [McpServerTool(Name = "operation_status", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationResult))]
        [Description("Read the durable result of a previous mutation by its request ID. Use this after timeouts or interrupted clients before attempting another mutation.")]
        public CallToolResult Status([Description("UUID returned by a previous create, attach or detach operation.")] string requestId)
            => Run("operation_status", new { requestId });

        private CallToolResult Run(string operation, object arguments) {
            var result = Operations.Execute(operation, JsonSerializer.SerializeToElement(arguments, ArgumentOptions));
            var json = JsonSerializer.SerializeToElement(result, Program.JsonOptions);
            return new CallToolResult {
                IsError = !result.Success,
                StructuredContent = json,
                Content = [new TextContentBlock { Text = json.GetRawText() }],
            };
        }
    }
}
