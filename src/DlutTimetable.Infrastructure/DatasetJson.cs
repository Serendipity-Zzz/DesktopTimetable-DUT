using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure;

public static class DatasetJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            WriteIndented = true,
            MaxDepth = 32
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
    public static ValidatedDataset Parse(ReadOnlyMemory<byte> utf8, string? expectedAccount = null, string? expectedTerm = null)
    {
        if (utf8.Length > WorkerProtocol.MaxMessageBytes) throw new InvalidDataException("课表文件超过 5 MiB 上限。");
        using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateKeys(document.RootElement);
        var data = document.Deserialize<CurrentDataset>(Options) ?? throw new JsonException("课表不能为空。");
        return ValidatedDataset.Create(data, expectedAccount, expectedTerm);
    }
    private static void RejectDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException("JSON 包含重复字段。");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) RejectDuplicateKeys(value);
    }
    public static byte[] Serialize(ValidatedDataset dataset)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dataset.Value, Options);
        if (bytes.Length > WorkerProtocol.MaxMessageBytes) throw new InvalidDataException("标准化课表超过 5 MiB 上限。");
        return bytes;
    }
    public static async Task<ValidatedDataset> ReadFileAsync(string path, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (file.Length > WorkerProtocol.MaxMessageBytes) throw new InvalidDataException("课表文件超过 5 MiB 上限。");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await file.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > WorkerProtocol.MaxMessageBytes) throw new InvalidDataException("课表文件超过 5 MiB 上限。");
            buffer.Write(chunk, 0, read);
        }
        // UTF-8 BOM is permitted in user-exported files.
        var bytes = buffer.ToArray();
        return Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? 3 : 0));
    }
}
