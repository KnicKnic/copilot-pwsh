// Minimal MCP stdio server for testing.
// Exposes 3 tools: test-server-alpha, test-server-beta, test-server-gamma.
// Each tool accepts a "message" parameter and echoes it back.
// Implements just enough of the MCP JSON-RPC protocol to be usable with
// the Copilot CLI.

using System.Text.Json;
using System.Text.Json.Nodes;

var tools = new[]
{
    new { name = "alpha", description = "Alpha test tool — echoes input" },
    new { name = "beta",  description = "Beta test tool — echoes input" },
    new { name = "gamma", description = "Gamma test tool — echoes input" },
};

while (true)
{
    var line = Console.ReadLine();
    if (line is null) break;

    JsonNode? request;
    try { request = JsonNode.Parse(line); }
    catch { continue; }

    var method = request?["method"]?.GetValue<string>();
    var id = request?["id"];

    JsonObject response;

    switch (method)
    {
        case "initialize":
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "test-mcp-server",
                        ["version"] = "1.0.0"
                    }
                }
            };
            break;

        case "notifications/initialized":
            continue; // notification — no response

        case "tools/list":
            var toolArray = new JsonArray();
            foreach (var t in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["name"] = t.name,
                    ["description"] = t.description,
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["message"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Message to echo"
                            }
                        },
                        ["required"] = new JsonArray("message")
                    }
                });
            }
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["tools"] = toolArray
                }
            };
            break;

        case "tools/call":
            var toolName = request?["params"]?["name"]?.GetValue<string>() ?? "unknown";
            var callArgs = request?["params"]?["arguments"];
            var msg = callArgs?["message"]?.GetValue<string>() ?? "";
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"[{toolName}] echo: {msg}"
                    })
                }
            };
            break;

        default:
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = $"Method not found: {method}"
                }
            };
            break;
    }

    Console.WriteLine(JsonSerializer.Serialize(response));
    Console.Out.Flush();
}
