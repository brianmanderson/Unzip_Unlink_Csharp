using System.IO.Compression;

namespace UnzipUnlink.Core;

public sealed record ZipProgress(string Archive, int EntriesDone, int EntriesTotal);

public static class ZipTools
{
    /// <summary>
    /// Extracts every .zip found under <paramref name="root"/> into a sibling folder named after the archive
    /// (<c>study.zip</c> → <c>study/</c>). Archives whose folder already exists are left untouched, matching
    /// the original tool. Returns the folders that were created.
    /// </summary>
    public static IReadOnlyList<string> ExtractAllInTree(
        string root, bool deleteArchives, IProgress<ZipProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var created = new List<string>();
        foreach (string zip in Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = DefaultExtractFolder(zip);
            if (Directory.Exists(destination))
            {
                continue;
            }
            Extract(zip, destination, progress, cancellationToken);
            created.Add(destination);
            if (deleteArchives)
            {
                File.Delete(zip);
            }
        }
        return created;
    }

    /// <summary><c>C:\x\study.zip</c> → <c>C:\x\study</c>.</summary>
    public static string DefaultExtractFolder(string zipPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zipPath))!, Path.GetFileNameWithoutExtension(zipPath));

    /// <summary>Extracts one archive, refusing any entry that would land outside <paramref name="destination"/>.</summary>
    public static void Extract(string zipPath, string destination, IProgress<ZipProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string destinationRoot = Path.GetFullPath(destination);
        string prefix = destinationRoot.EndsWith(Path.DirectorySeparatorChar) ? destinationRoot : destinationRoot + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destinationRoot);
        string name = Path.GetFileName(zipPath);
        var gate = new ProgressGate();

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        int total = archive.Entries.Count, done = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' would extract outside the destination folder.");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            done++;
            if (gate.ShouldReport(done, total))
            {
                progress?.Report(new ZipProgress(name, done, total));
            }
        }
    }

    /// <summary>Streams the contents of <paramref name="root"/> as a zip archive into <paramref name="output"/>.</summary>
    public static async Task WriteZipAsync(string root, Stream output, CancellationToken cancellationToken = default)
    {
        string rootFull = Path.GetFullPath(root);
        await using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (string file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryName = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            await using Stream entryStream = await entry.OpenAsync(cancellationToken);
            await using FileStream source = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await source.CopyToAsync(entryStream, 1 << 16, cancellationToken);
        }
    }
}
