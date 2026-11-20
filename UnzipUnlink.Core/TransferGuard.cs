namespace UnzipUnlink.Core;

/// <summary>
/// Detects folders and files that are still being copied (for example a study arriving over the network),
/// so processing does not start on a partial transfer. Replaces the old FileSystemWatcher + fixed sleeps.
/// </summary>
public static class TransferGuard
{
    /// <summary>
    /// Returns true once the folder has looked identical for <paramref name="quietChecks"/> consecutive
    /// intervals (default: two one-second intervals, so a slow trickle of roughly a file a second is still
    /// caught); false if it is still changing when <paramref name="timeout"/> elapses.
    /// </summary>
    public static bool WaitUntilStable(string directory, TimeSpan? interval = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, int quietChecks = 2)
    {
        TimeSpan step = interval ?? TimeSpan.FromSeconds(1);
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        return WaitUntilStable(
            () => Snapshot.Take(directory),
            () =>
            {
                if (cancellationToken.WaitHandle.WaitOne(step))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return DateTime.UtcNow < deadline;
            },
            quietChecks);
    }

    /// <summary>
    /// The decision logic on its own, with the snapshot source and the clock supplied by the caller so it can
    /// be tested without real files or real time. <paramref name="waitForNextCheck"/> waits one interval and
    /// returns false once the deadline has passed.
    /// </summary>
    internal static bool WaitUntilStable<T>(Func<T> takeSnapshot, Func<bool> waitForNextCheck, int quietChecks)
        where T : IEquatable<T>
    {
        T previous = takeSnapshot();
        int quiet = 0;
        while (true)
        {
            bool timeLeft = waitForNextCheck();
            T current = takeSnapshot();
            quiet = current.Equals(previous) ? quiet + 1 : 0;
            if (quiet >= Math.Max(1, quietChecks))
            {
                return true;
            }
            if (!timeLeft)
            {
                return false;
            }
            previous = current;
        }
    }

    /// <summary>True while another process still holds the file open for writing.</summary>
    public static bool IsFileLocked(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only file: not locked, just not writable.
            return false;
        }
    }

    /// <summary>Waits for a single file to be released; false if it is still locked at the timeout.</summary>
    public static bool WaitUntilUnlocked(string path, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (IsFileLocked(path))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return true;
    }

    private readonly record struct Snapshot(int Files, long Bytes, long LatestWriteTicks)
    {
        public static Snapshot Take(string directory)
        {
            int files = 0;
            long bytes = 0, latest = 0;
            foreach (FileInfo info in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                files++;
                try
                {
                    bytes += info.Length;
                    latest = Math.Max(latest, info.LastWriteTimeUtc.Ticks);
                }
                catch (IOException)
                {
                    // File vanished mid-enumeration: that alone makes the next snapshot differ.
                }
            }
            return new Snapshot(files, bytes, latest);
        }
    }
}
