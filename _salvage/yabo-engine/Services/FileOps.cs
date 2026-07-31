using System.IO;

namespace GithubLauncher.Services
{
    /// <summary>
    /// [yabo-launcher fork] Filesystem helpers shared by install/uninstall paths.
    /// </summary>
    public static class FileOps
    {
        /// <summary>
        /// Deletes a directory tree robustly. A plain Directory.Delete(recursive) throws
        /// UnauthorizedAccessException on read-only files — common with bundled JRE/runtime files
        /// (e.g. QuakeInjector), which is why "uninstall" silently failed. This clears read-only
        /// attributes across the tree, then deletes, retrying a few times to ride out transient locks
        /// (antivirus/indexer/handle release). Logs each attempt; rethrows the final failure so the
        /// caller can surface/log the real reason.
        /// </summary>
        public static void DeleteDirectoryResilient(string path)
        {
            if (!Directory.Exists(path))
                return;

            Exception? last = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    ClearReadOnly(new DirectoryInfo(path));
                    Directory.Delete(path, recursive: true);
                    return; // success
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Warn($"Delete attempt {attempt}/4 for '{path}' failed: {ex.GetType().Name}: {ex.Message}");
                    System.Threading.Thread.Sleep(250 * attempt);
                }
            }

            // Still here → couldn't delete. Surface which files are likely holding it.
            try
            {
                var leftover = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(5).ToList();
                if (leftover.Count > 0)
                    Log.Error($"Could not fully delete '{path}'. Sample remaining files: {string.Join("; ", leftover)}");
            }
            catch { /* ignore */ }

            throw last ?? new IOException($"Failed to delete '{path}'.");
        }

        private static void ClearReadOnly(DirectoryInfo dir)
        {
            try
            {
                foreach (var file in dir.GetFiles())
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                        file.Attributes = FileAttributes.Normal;
                }
                foreach (var sub in dir.GetDirectories())
                    ClearReadOnly(sub);
                if ((dir.Attributes & FileAttributes.ReadOnly) != 0)
                    dir.Attributes = FileAttributes.Normal;
            }
            catch
            {
                // Best-effort; the delete attempt below will report the real error.
            }
        }
    }
}
