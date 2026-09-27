using System.Security.Cryptography;
using System.Text.Json;

namespace PsfGuard.Director.Runtime;

// Local state is a retry/inspection aid, never an equipment permit or a secret store.
internal sealed class CoordinatorStateFile
{
    internal string Path { get; }
    internal CoordinatorStateFile(string root, object scope, string kind)
    {
        if (!System.IO.Path.IsPathFullyQualified(root)) throw new ArgumentException("State root must be absolute.", nameof(root));
        var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope, CoordinatorProgramContract.Options)));
        Directory.CreateDirectory(root);
        Path = System.IO.Path.Combine(root, $"{kind}-{key}.json");
    }

    internal FileStream Lock() => new(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal T? Read<T>() where T : class
    {
        try
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > CoordinatorProgramContract.MaximumBytes * 2) throw new InvalidDataException("Director state exceeds its size limit.");
            using var bytes = new MemoryStream();
            file.CopyTo(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray());
            CoordinatorProgramContract.CheckTree(document.RootElement);
            return document.RootElement.Deserialize<T>(CoordinatorProgramContract.Options) ?? throw new InvalidDataException("Empty Director state.");
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Invalid Director state."); }
    }

    internal void Write<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, CoordinatorProgramContract.Options);
        if (bytes.Length > CoordinatorProgramContract.MaximumBytes * 2) throw new InvalidDataException("Director state exceeds its size limit.");
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
