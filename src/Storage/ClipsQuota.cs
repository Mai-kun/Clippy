namespace Clippy;

/// <summary>
/// Keeps the clips folder under a size ceiling by deleting the oldest clips first.
/// </summary>
/// <remarks>
/// Clips accumulate silently. A user does not notice that folder growing until something unrelated
/// fails on a full disk, and by then it is a surprise rather than a policy. This turns that surprise
/// into something the user chose, with the oldest going first because the newest is the one they are
/// most likely to still want.
/// <para>
/// Only *.mp4 is counted and only *.mp4 is deleted. The folder may legitimately hold other files, and
/// deleting a user's unrelated downloads to satisfy a video quota is not a trade worth making.
/// <para>
/// A ceiling of 0 disables all of this, which is the default: a quota that deletes files by default
/// would be a dangerous default to ship.
/// </remarks>
internal static class ClipsQuota
{
    /// <summary>
    /// Enforces the ceiling. Returns how many files were deleted, for the log and the toast.
    /// </summary>
    public static int Enforce(string folder, int maxSizeGB)
    {
        if (maxSizeGB <= 0)
            return 0;

        try
        {
            if (!Directory.Exists(folder))
                return 0;

            var limit = (long)maxSizeGB * 1024 * 1024 * 1024;
            var clips = new DirectoryInfo(folder)
                .GetFiles("*.mp4", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            long total = clips.Sum(f => f.Length);
            if (total <= limit)
                return 0;

            int deleted = 0;
            foreach (var clip in clips)
            {
                if (total <= limit)
                    break;

                long size = clip.Length;
                try
                {
                    // The usual suspects for a sharing violation are the folder being open in Explorer
                    // or a player holding the file. Skipping and continuing is the right call: the next
                    // save will try again, and refusing to delete anything at all would leave the folder
                    // over quota for good.
                    clip.Delete();
                    total -= size;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"Quota: could not delete {clip.Name} ({ex.GetType().Name}).");
                }
            }

            if (deleted > 0)
                Console.WriteLine($"Quota: deleted {deleted} old clip(s), now {total / (1024.0 * 1024 * 1024):F1} GB.");
            return deleted;
        }
        catch (Exception ex)
        {
            // A quota that cannot be evaluated must not stop a recording. Log it and carry on.
            Console.WriteLine($"Quota: check failed ({ex.GetType().Name}: {ex.Message}).");
            return 0;
        }
    }
}