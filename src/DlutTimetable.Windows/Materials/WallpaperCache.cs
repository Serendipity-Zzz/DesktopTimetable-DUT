namespace DlutTimetable.Windows.Materials;

public sealed class WallpaperCache
{
    public const long BudgetBytes = 16 * 1024 * 1024;
    private readonly object gate = new();
    private readonly LinkedList<MaterialFrame> frames = [];
    private WallpaperPixels? source;
    private long generation;
    public long Generation { get { lock(gate) return generation; } }
    public long Bytes { get { lock(gate) return BytesCore(); } }
    public int Count { get { lock(gate) return frames.Count; } }
    private long BytesCore() => (source?.ByteCount ?? 0) + frames.Sum(f => f.ByteCount);
    public WallpaperPixels? GetSource(string id) { lock(gate) return source?.SourceId == id ? source : null; }
    public bool SetSource(WallpaperPixels pixels, long expectedGeneration)
    {
        lock(gate)
        {
            if (generation != expectedGeneration || pixels.ByteCount > BudgetBytes) return false;
            if (source?.SourceId != pixels.SourceId) frames.Clear();
            source = pixels; Trim(); return true;
        }
    }
    public MaterialFrame? Get(MaterialKey key)
    {
        lock(gate)
        {
            var node = frames.First;
            while(node is not null)
            {
                if (node.Value.Key == key) { var value = node.Value; frames.Remove(node); frames.AddFirst(value); return value; }
                node = node.Next;
            }
            return null;
        }
    }
    public bool Add(MaterialFrame frame, long expectedGeneration)
    {
        lock(gate)
        {
            if (generation != expectedGeneration || frame.ByteCount + (source?.ByteCount ?? 0) > BudgetBytes) return false;
            var node = frames.First;
            while(node is not null) { var next=node.Next;if(node.Value.Key==frame.Key)frames.Remove(node);node=next; }
            frames.AddFirst(frame); Trim(); return true;
        }
    }
    private void Trim() { while(frames.Count > 3 || BytesCore() > BudgetBytes) frames.RemoveLast(); }
    public void Clear() { lock(gate) { generation++; frames.Clear(); source=null; } }
}
