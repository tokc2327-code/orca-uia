// Minimal MCP server over stdio (JSON-RPC 2.0, newline-delimited). No MCP SDK dependency.
// UIA3 requires an STA thread, so the Engine lives on one; that same thread owns a Win32
// message queue which we pump between requests (see Pump below). Some COM/UIA calls post
// messages back to the calling apartment and will time out if nobody pumps them.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrcaUia;

var queue = new BlockingCollection<string?>();

var worker = new Thread(() => Run(queue));
worker.SetApartmentState(ApartmentState.STA);
worker.Start();

// Reader thread (this one) does nothing but feed lines in, so a slow tool call never
// blocks the pipe and the client can always write.
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
string? line;
while ((line = stdin.ReadLine()) != null) queue.Add(line);
queue.Add(null);
worker.Join();

static void Run(BlockingCollection<string?> queue)
{
    // Touching the queue once here is what materialises this thread's Win32 message queue
    // before any COM object is created on it.
    Pump.Drain();

    using var engine = new Engine();
    var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

    while (true)
    {
        Pump.Drain();
        if (!queue.TryTake(out var raw, 10))
        {
            if (queue.IsCompleted) { Pump.Drain(); break; }
            continue;
        }
        if (raw == null) break;
        if (string.IsNullOrWhiteSpace(raw)) continue;

        JsonNode? req;
        try { req = JsonNode.Parse(raw); } catch { continue; }
        if (req == null) continue;

        var id = req["id"];
        var method = req["method"]?.GetValue<string>() ?? "";
        if (id == null) continue; // notifications (e.g. notifications/initialized): no reply

        JsonNode? result = null;
        JsonObject? error = null;
        try
        {
            switch (method)
            {
                case "initialize":
                    result = new JsonObject
                    {
                        ["protocolVersion"] = req["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "orca-uia", ["version"] = "0.1.0" }
                    };
                    break;
                case "ping":
                    result = new JsonObject();
                    break;
                case "tools/list":
                    result = new JsonObject { ["tools"] = Tools.Definitions() };
                    break;
                case "tools/call":
                    var name = req["params"]?["name"]?.GetValue<string>() ?? "";
                    var args = req["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    // Pump on both sides of a call: messages that piled up while the previous
                    // tool was running get dispatched, and anything the call itself posted is
                    // drained before the next one starts.
                    Pump.Drain();
                    result = CallTool(engine, name, args);
                    Pump.Drain();
                    break;
                default:
                    error = new JsonObject { ["code"] = -32601, ["message"] = "method not found: " + method };
                    break;
            }
        }
        catch (Exception ex)
        {
            error = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
        }

        var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (error != null) resp["error"] = error; else resp["result"] = result;
        stdout.WriteLine(resp.ToJsonString());
    }

    stdout.Flush();
}

static JsonNode CallTool(Engine engine, string name, JsonObject args)
{
    try
    {
        string text = engine.Dispatch(name, args);
        return Wrap(text, false);
    }
    catch (ToolError te)
    {
        // Structured, machine-readable failure so the agent can pick a recovery action.
        return Wrap(JsonSerializer.Serialize(new { error = te.Code, message = te.Message, hint = te.Hint }), true);
    }
    catch (Exception ex)
    {
        return Wrap(JsonSerializer.Serialize(new { error = "INTERNAL", message = ex.Message }), true);
    }
}

static JsonNode Wrap(string text, bool isError) => new JsonObject
{
    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    ["isError"] = isError
};

// Win32 message pump for the STA thread. UIA/COM deliver cross-apartment calls as posted
// window messages; without a pump those calls sit in the queue until a timeout fires.
static class Pump
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    private const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    /// <summary>Dispatch everything currently queued. Must be called on the owning thread.</summary>
    public static void Drain()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }
}
