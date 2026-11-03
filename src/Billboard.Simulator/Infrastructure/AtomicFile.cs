using System.Text;

namespace Billboard.Simulator;

/// <summary>Атомарная запись файлов: сначала временный файл, затем подмена.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный файл мог остаться из-за блокировки — это не критично.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
