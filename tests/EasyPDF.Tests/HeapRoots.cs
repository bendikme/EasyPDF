using Microsoft.Diagnostics.Runtime;

/// <summary>
/// Explains a leak: takes a snapshot of this process and finds, for each live object of a type,
/// the chain of references from a GC root that keeps it alive.
/// </summary>
internal static class HeapRoots
{
    /// <summary>Live objects per type: count and bytes, for finding what grows between two moments.</summary>
    public static Dictionary<string, (int Count, long Bytes)> Census()
    {
        var census = new Dictionary<string, (int Count, long Bytes)>();
        using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        var heap = runtime.Heap;

        // Only what a root reaches is alive.
        var seen = new HashSet<ulong>();
        var stack = new Stack<ulong>();
        foreach (var root in heap.EnumerateRoots())
            if (root.Object.Address != 0 && seen.Add(root.Object.Address)) stack.Push(root.Object.Address);
        while (stack.Count > 0)
        {
            var obj = heap.GetObject(stack.Pop());
            var name = obj.Type?.Name ?? "?";
            var (count, bytes) = census.GetValueOrDefault(name);
            census[name] = (count + 1, bytes + (long)obj.Size);
            foreach (var child in obj.EnumerateReferences())
                if (child.Address != 0 && seen.Add(child.Address)) stack.Push(child.Address);
        }
        return census;
    }

    public static string Explain(string typeName, int maxObjects = 3)
    {
        try
        {
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            using var runtime = target.ClrVersions[0].CreateRuntime();
            var heap = runtime.Heap;

            var targets = heap.EnumerateObjects().Where(o => o.Type?.Name == typeName).Select(o => o.Address).Take(maxObjects).ToHashSet();
            if (targets.Count == 0) return $"no {typeName} on the heap";

            // Breadth first from every root; the first path to reach a target is a shortest one.
            var parent = new Dictionary<ulong, ulong>();
            var rootOf = new Dictionary<ulong, string>();
            var queue = new Queue<ulong>();
            foreach (var root in heap.EnumerateRoots())
            {
                var address = root.Object.Address;
                if (address == 0 || rootOf.ContainsKey(address)) continue;
                rootOf[address] = root.RootKind.ToString();
                parent[address] = 0;
                queue.Enqueue(address);
            }

            var lines = new List<string>();
            var found = new HashSet<ulong>();
            while (queue.Count > 0 && found.Count < targets.Count)
            {
                var address = queue.Dequeue();
                if (targets.Contains(address) && found.Add(address))
                {
                    var chain = new List<string>();
                    for (var a = address; a != 0; a = parent[a])
                        chain.Add(heap.GetObject(a).Type?.Name ?? "?");
                    chain.Reverse();
                    var start = chain.Count > 0 ? rootOf.GetValueOrDefault(FirstOf(address, parent), "?") : "?";
                    lines.Add($"[{start}] " + string.Join(" -> ", chain));
                }

                foreach (var child in heap.GetObject(address).EnumerateReferences())
                {
                    if (child.Address == 0 || parent.ContainsKey(child.Address)) continue;
                    parent[child.Address] = address;
                    queue.Enqueue(child.Address);
                }
            }
            return lines.Count == 0 ? $"{typeName}: reachable from no root (it will be collected)" : string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"(the heap could not be read: {ex.Message})";
        }
    }

    private static ulong FirstOf(ulong address, Dictionary<ulong, ulong> parent)
    {
        while (parent[address] != 0) address = parent[address];
        return address;
    }
}
