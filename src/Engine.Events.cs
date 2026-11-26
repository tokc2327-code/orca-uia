using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;

namespace OrcaUia;

public partial class Engine
{
    const int EventQueueLimit = 1024;
    const int JournalLimit = 2048;
    static readonly string[] EventKinds = ["structure", "property", "appear", "gone", "focus"];
    readonly Dictionary<string, Watch> watches = new();
    readonly Dictionary<string, string> stoppedWatches = new();
    AutomationElement? lastSnapshotWindow;
    long snapshotGeneration;

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);

    sealed record NodeInfo(string? id, string? aid, string? name, string? type, string? runtime_id, long? snapshot, bool unmatched);
    sealed record WatchedWindow(int pid, string? title, long hwnd);
    sealed record Item(long seq, DateTimeOffset ts, string kind, WatchedWindow window, NodeInfo node, object? change, string confidence);
    sealed record Raw(AutomationElement sender, string kind, string? property, object? value, DateTimeOffset ts);
    sealed record Pending(NodeInfo Node, string Kind, string Property, object? Before, object? After, DateTimeOffset Ts, int Count);

    sealed class Watch
    {
        public required string Id;
        public required AutomationElement Root;
        public required WatchedWindow Window;
        public required HashSet<string> Kinds;
        public required int DebounceMs;
        public required DateTimeOffset Expires;
        public readonly ConcurrentQueue<Raw> Inbox = new();
        public readonly List<Action> Unregister = [];
        public readonly Queue<Item> Journal = new();
        public readonly Dictionary<(string, string, string), Pending> Pending = new();
        public readonly Dictionary<(string, string), object?> Values = new();
        public Dictionary<string, NodeInfo> Tree = new();
        public volatile bool Active = true;
        public int Queued, RawCount, RawDropped;
        public int Coalesced;
        public long Seq;
        public long Unmatched;
        public long Evicted;
        public DateTimeOffset? FlushAt;
        public Raw? LastStructure;
        public int StructureCount;
        public Raw? LastIdentitySignal;

        public void Push(AutomationElement sender, string kind, string? property = null, object? value = null)
        {
            if (!Active) return;
            Interlocked.Increment(ref RawCount);
            if (Interlocked.Increment(ref Queued) > EventQueueLimit)
            {
                Interlocked.Decrement(ref Queued);
                Interlocked.Increment(ref RawDropped);
                return;
            }
            Inbox.Enqueue(new Raw(sender, kind, property, value, DateTimeOffset.UtcNow));
        }
    }

    static string Cursor(Watch watch, long seq) => $"{watch.Id}:{seq}";

    void MarkSnapshot()
    {
        lastSnapshotWindow = win;
        snapshotGeneration++;
    }

    string SnapshotDelta(JsonObject args) { EventsTick(); return PollEvents(args, true); }

    string Events(JsonObject args)
    {
        EventsTick();
        return args["action"]?.GetValue<string>() switch
        {
            "subscribe" => Subscribe(args),
            "poll" => PollEvents(args, false),
            "unsubscribe" => Unsubscribe(args),
            "status" => EventStatus(args),
            _ => throw new ToolError("BAD_ARGS", "action must be subscribe, poll, unsubscribe or status")
        };
    }

    string Subscribe(JsonObject args)
    {
        if (watches.Count >= 4) throw new ToolError("EVENTS_BUSY", "at most four active subscriptions");
        if (args["window"] is not JsonObject filter)
            throw new ToolError("BAD_ARGS", "window must contain pid or title_contains");
        int? pid = filter["pid"]?.GetValue<int>();
        string? title = filter["title_contains"]?.GetValue<string>();
        if (pid is not > 0 && string.IsNullOrWhiteSpace(title))
            throw new ToolError("BAD_ARGS", "window must contain pid or title_contains");
        var root = FindWindow(pid, title) ?? throw new ToolError("WINDOW_NOT_FOUND", "no top-level window matched");
        var requested = args["kinds"] is JsonArray arr ? arr.Select(v => v?.GetValue<string>() ?? "").ToArray() : EventKinds;
        if (requested.Length == 0 || requested.Any(kind => !EventKinds.Contains(kind)))
            throw new ToolError("BAD_ARGS", "unsupported or empty kinds");
        var handle = new IntPtr(root.Properties.NativeWindowHandle.ValueOrDefault);
        if (handle == IntPtr.Zero) throw new ToolError("WINDOW_NOT_FOUND", "window has no HWND");
        var debounce = Bounded(args, "debounce_ms", 80, 0, 1000);
        int lease = Bounded(args, "lease_s", 60, 1, 900);
        var watch = new Watch
        {
            Id = Guid.NewGuid().ToString("N"), Root = root,
            Window = new(root.Properties.ProcessId.ValueOrDefault, root.Properties.Name.ValueOrDefault, handle.ToInt64()),
            Kinds = new HashSet<string>(requested), DebounceMs = debounce, Expires = DateTimeOffset.UtcNow.AddSeconds(lease)
        };
        try
        {
            if (watch.Kinds.Overlaps(["structure", "appear", "gone"]))
            {
                watch.Tree = EventTree(root);
                var handler = root.RegisterStructureChangedEvent(TreeScope.Subtree, (sender, _, _) => watch.Push(sender, "structure"));
                watch.Unregister.Add(() => root.FrameworkAutomationElement.UnregisterStructureChangedEventHandler(handler));
            }
            if (watch.Kinds.Overlaps(["property", "focus"]))
            {
                var prop = auto.PropertyLibrary;
                var focus = prop.Element.HasKeyboardFocus;
                var selected = new List<PropertyId>();
                if (watch.Kinds.Contains("property"))
                    selected.AddRange([prop.Element.Name, prop.Element.IsEnabled, prop.Element.IsOffscreen, prop.Value.Value, prop.RangeValue.Value]);
                if (watch.Kinds.Contains("focus")) selected.Add(focus);
                var propertyHandler = root.RegisterPropertyChangedEvent(TreeScope.Subtree,
                    (sender, property, value) => watch.Push(sender, property == focus ? "focus" : "property",
                        property.Name, Scalar(value)), selected.ToArray());
                watch.Unregister.Add(() => root.FrameworkAutomationElement.UnregisterPropertyChangedEventHandler(propertyHandler));
                if (watch.Kinds.Contains("property"))
                {
                    var text = root.RegisterAutomationEvent(auto.EventLibrary.Text.TextChangedEvent, TreeScope.Subtree,
                        (sender, _) => watch.Push(sender, "property", "Text"));
                    watch.Unregister.Add(() => root.FrameworkAutomationElement.UnregisterAutomationEventHandler(text));
                }
            }
        }
        catch
        {
            DetachEvents(watch);
            throw;
        }
        watches.Add(watch.Id, watch);
        return Json(new { subscription_id = watch.Id, cursor = Cursor(watch, 0), window = watch.Window, kinds = requested,
            debounce_ms = debounce, expires = watch.Expires, registered_handlers = watch.Unregister.Count });
    }

    static int Bounded(JsonObject args, string key, int fallback, int low, int high)
    {
        var num = args[key]?.GetValue<int>() ?? fallback;
        if (num < low || num > high) throw new ToolError("BAD_ARGS", $"{key} must be {low}..{high}");
        return num;
    }

    static object? Scalar(object? raw) => raw switch
    {
        string value => value.Length <= 512 ? value : value[..512],
        bool or int or long => raw,
        double value when double.IsFinite(value) => value,
        _ => null
    };

    static bool WindowAlive(Watch watch)
    {
        var handle = new IntPtr(watch.Window.hwnd);
        return IsWindow(handle) && GetWindowThreadProcessId(handle, out var pid) != 0 && pid == (uint)watch.Window.pid;
    }

    Watch GetWatch(string id)
    {
        if (watches.TryGetValue(id, out var item)) return item;
        if (stoppedWatches.TryGetValue(id, out var status))
            throw new ToolError(status, "subscription is no longer active");
        throw new ToolError("EVENTS_UNKNOWN_SUBSCRIPTION", "subscription_id or cursor required");
    }

    void StopWatch(Watch watch, string reason)
    {
        watches.Remove(watch.Id);
        stoppedWatches[watch.Id] = reason;
        if (stoppedWatches.Count > 64) stoppedWatches.Remove(stoppedWatches.Keys.First());
        DetachEvents(watch);
    }

    static int DetachEvents(Watch watch)
    {
        watch.Active = false;
        int failed = 0;
        foreach (var remove in watch.Unregister)
            try { remove(); } catch { failed++; }
        while (watch.Inbox.TryDequeue(out _)) { }
        return failed;
    }

    string Unsubscribe(JsonObject args)
    {
        var watch = GetWatch(args["subscription_id"]?.GetValue<string>() ?? "");
        var count = watch.Unregister.Count;
        watches.Remove(watch.Id);
        stoppedWatches[watch.Id] = "EVENTS_UNSUBSCRIBED";
        if (stoppedWatches.Count > 64) stoppedWatches.Remove(stoppedWatches.Keys.First());
        int failures = DetachEvents(watch);
        if (failures != 0) throw new ToolError("EVENTS_CLEANUP_FAILED", $"{failures} UIA handlers could not be removed");
        return Json(new { ok = true, subscription_id = watch.Id, removed_handlers = count, raw_events = watch.RawCount });
    }

    string EventStatus(JsonObject args)
    {
        if (args["subscription_id"]?.GetValue<string>() is string id)
            return Json(StatusOf(GetWatch(id)));
        return Json(new { active_subscriptions = watches.Count,
            registered_handlers = watches.Values.Sum(w => w.Unregister.Count),
            subscriptions = watches.Values.Select(StatusOf).ToArray() });
    }

    static object StatusOf(Watch watch) => new
    {
        subscription_id = watch.Id, window = watch.Window, cursor = Cursor(watch, watch.Seq), expires = watch.Expires,
        registered_handlers = watch.Unregister.Count, raw_events = watch.RawCount, pending_raw = watch.Queued,
        coalesced = watch.Coalesced, emitted = watch.Seq, unmatched = watch.Unmatched,
        dropped_raw = watch.RawDropped, evicted = watch.Evicted
    };

    string PollEvents(JsonObject args, bool delta)
    {
        var cursor = args["since"]?.GetValue<string>();
        string? id = args["subscription_id"]?.GetValue<string>();
        long seq = 0;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[1], out seq) || seq < 0 || id != null && id != parts[0])
                throw new ToolError("EVENTS_BAD_CURSOR", "cursor does not belong to this subscription");
            id = parts[0];
        }
        var watch = GetWatch(id ?? "");
        if (seq > watch.Seq) throw new ToolError("EVENTS_BAD_CURSOR", "cursor is ahead of the journal");
        if (seq < watch.Evicted || watch.RawDropped > 0)
            throw new ToolError("EVENTS_CURSOR_STALE", "event loss detected; refresh snapshot and subscribe again");
        int limit = Bounded(args, "limit", 64, 1, 256);
        int maxBytes = Bounded(args, "max_bytes", 16384, 512, 262144);
        var items = new List<Item>();
        long next = seq;
        foreach (var item in watch.Journal)
        {
            if (item.seq <= seq) continue;
            if (delta && item.kind is not ("appear" or "gone" or "property"))
            {
                next = item.seq;
                continue;
            }
            if (items.Count == limit) break;
            items.Add(item);
            string provisional = EncodePage(items, item.seq, watch, delta);
            if (Encoding.UTF8.GetByteCount(provisional) > maxBytes)
            {
                items.RemoveAt(items.Count - 1);
                if (items.Count == 0)
                    throw new ToolError("EVENTS_MAX_BYTES", "increase max_bytes for the next event");
                break;
            }
            next = item.seq;
        }
        return EncodePage(items, next, watch, delta);
    }

    static string EncodePage(List<Item> items, long seq, Watch watch, bool delta)
    {
        var root = new JsonObject
        {
            [delta ? "changes" : "events"] = JsonSerializer.SerializeToNode(items),
            ["cursor"] = Cursor(watch, seq),
            ["unmatched"] = items.Count(item => item.node.unmatched),
            ["has_more"] = seq < watch.Seq
        };
        return root.ToJsonString();
    }

    static NodeInfo Describe(AutomationElement el)
    {
        try
        {
            var key = el.FrameworkAutomationElement.RuntimeId.ValueOrDefault;
            return new NodeInfo(null, el.Properties.AutomationId.ValueOrDefault, el.Properties.Name.ValueOrDefault,
                el.Properties.ControlType.ValueOrDefault.ToString(), key is { Length: > 0 } ? string.Join(".", key) : null, null, true);
        }
        catch { return new NodeInfo(null, null, null, null, null, null, true); }
    }

    Dictionary<string, NodeInfo> EventTree(AutomationElement root)
    {
        var tree = new Dictionary<string, NodeInfo>();
        var todo = new Stack<AutomationElement>();
        todo.Push(root);
        var visited = 0;
        while (todo.Count > 0 && visited++ < 600)
        {
            var node = todo.Pop();
            try
            {
                // RuntimeId must be read outside the legacy property cache.
                var description = Describe(node);
                if (!node.Properties.IsOffscreen.ValueOrDefault && description.runtime_id is string key) tree[key] = description;
                foreach (var child in node.FindAllChildren().Take(600 - todo.Count)) todo.Push(child);
            }
            catch { /* providers may disappear while traversing */ }
        }
        return tree;
    }

    NodeInfo Align(Watch watch, NodeInfo node)
    {
        if (lastSnapshotWindow is null || node.type is null || recipes.Count == 0) return node;
        try
        {
            if (lastSnapshotWindow.Properties.NativeWindowHandle.ValueOrDefault != watch.Window.hwnd) return node;
            var hits = recipes.Where(entry => entry.Value.Type.ToString() == node.type
                && (!string.IsNullOrEmpty(node.aid) ? entry.Value.AutoId == node.aid
                    : !string.IsNullOrEmpty(node.name) && entry.Value.Name == node.name)).Take(2).ToArray();
            return hits.Length == 1 ? node with { id = hits[0].Key, snapshot = snapshotGeneration, unmatched = false } : node;
        }
        catch { return node; }
    }

    void Publish(Watch watch, DateTimeOffset ts, string kind, NodeInfo node, object? change, string confidence)
    {
        if (!watch.Kinds.Contains(kind)) return;
        node = Align(watch, node);
        if (node.unmatched) watch.Unmatched++;
        if (watch.Journal.Count == JournalLimit) watch.Evicted = watch.Journal.Dequeue().seq;
        watch.Journal.Enqueue(new Item(++watch.Seq, ts, kind, watch.Window, node, change, confidence));
    }

    static object? ObservedValue(Raw raw)
    {
        try
        {
            // Unknown password status is treated as sensitive for value/text signals.
            if (raw.property?.Contains("Value", StringComparison.OrdinalIgnoreCase) == true || raw.property == "Text")
            {
                if (raw.sender.Properties.IsPassword.Value) return null;
                if (raw.value is not null) return raw.value;
                if (raw.sender.Patterns.Value.IsSupported)
                    return Scalar(raw.sender.Patterns.Value.Pattern.Value.ValueOrDefault);
                if (raw.property == "Text" && raw.sender.Patterns.Text.IsSupported)
                    return Scalar(raw.sender.Patterns.Text.Pattern.DocumentRange.GetText(512));
            }
            return raw.value;
        }
        catch { return null; }
    }

    void Stage(Watch watch, Raw raw)
    {
        watch.FlushAt ??= raw.ts.AddMilliseconds(watch.DebounceMs);
        if (raw.kind == "structure")
        {
            watch.StructureCount++;
            watch.LastStructure = raw;
            return;
        }
        var node = Describe(raw.sender);
        string property = raw.property ?? "";
        if (watch.Kinds.Overlaps(["appear", "gone"]) && property is "Name" or "IsOffscreen")
            watch.LastIdentitySignal = raw;
        if (node.runtime_id is null)
        {
            Publish(watch, raw.ts, raw.kind, node, new { property }, "direct");
            return;
        }
        var key = (node.runtime_id, raw.kind, property);
        var value = ObservedValue(raw);
        if (watch.Pending.TryGetValue(key, out var old))
        {
            watch.Pending[key] = old with { After = value, Ts = raw.ts, Count = old.Count + 1 };
            watch.Coalesced++;
            return;
        }
        watch.Values.TryGetValue((node.runtime_id, property), out var before);
        watch.Pending[key] = new Pending(node, raw.kind, property, before, value, raw.ts, 1);
    }

    void Flush(Watch watch)
    {
        foreach (var item in watch.Pending.Values.OrderBy(item => item.Ts))
        {
            var change = new Dictionary<string, object?> { ["property"] = item.Property };
            if (item.Before is not null) change["before"] = item.Before;
            if (item.After is not null) change["after"] = item.After;
            if (watch.Values.Count >= JournalLimit) watch.Values.Clear();
            if (item.Node.runtime_id is string key) watch.Values[(key, item.Property)] = item.After;
            Publish(watch, item.Ts, item.Kind, item.Node, change, item.Count > 1 ? "merged" : "direct");
        }
        watch.Pending.Clear();
        if ((watch.LastStructure ?? watch.LastIdentitySignal) is Raw raw)
        {
            var old = watch.Tree;
            var tree = EventTree(watch.Root);
            foreach (var pair in tree.Where(pair => !old.ContainsKey(pair.Key)))
                Publish(watch, raw.ts, "appear", pair.Value, new { after = pair.Value }, "reconciled");
            foreach (var pair in old.Where(pair => !tree.ContainsKey(pair.Key)))
                Publish(watch, raw.ts, "gone", pair.Value, new { before = pair.Value }, "reconciled");
            if (watch.LastStructure is Raw source)
            {
                Publish(watch, source.ts, "structure", Describe(source.sender), new { notifications = watch.StructureCount },
                    watch.StructureCount > 1 ? "merged" : "direct");
                watch.Coalesced += watch.StructureCount - 1;
            }
            watch.Tree = tree;
            watch.LastStructure = null;
            watch.LastIdentitySignal = null;
            watch.StructureCount = 0;
        }
        watch.FlushAt = null;
    }

    public void EventsTick()
    {
        if (watches.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var watch in watches.Values.ToArray())
        {
            if (now >= watch.Expires) { StopWatch(watch, "EVENTS_LEASE_EXPIRED"); continue; }
            if (!WindowAlive(watch)) { StopWatch(watch, "EVENTS_TARGET_GONE"); continue; }
            for (int i = 0; i < 32 && watch.Inbox.TryDequeue(out var raw); i++)
            {
                Interlocked.Decrement(ref watch.Queued);
                if (watch.FlushAt is DateTimeOffset due && raw.ts >= due) Flush(watch);
                Stage(watch, raw);
            }
            if (watch.FlushAt is DateTimeOffset deadline && now >= deadline && watch.Inbox.IsEmpty) Flush(watch);
        }
    }

    void DisposeEvents()
    {
        foreach (var watch in watches.Values.ToArray()) StopWatch(watch, "EVENTS_UNSUBSCRIBED");
    }
}
