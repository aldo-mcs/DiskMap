namespace DiskMap.Core.Cleanup;

/// <summary>Resilient recursive file enumeration shared by the cleanup scanner and cleaner —
/// skips directories it can't access instead of aborting the whole walk.</summary>
internal static class CleanupFileWalker
{
    public static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files = [];
            IEnumerable<string> subDirs = [];
            try { files = Directory.EnumerateFiles(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { subDirs = Directory.EnumerateDirectories(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            foreach (var f in files) yield return f;
            foreach (var d in subDirs) stack.Push(d);
        }
    }

    /// <summary>Removes now-empty subdirectories under root (deepest first), leaving root itself intact.</summary>
    public static void RemoveEmptySubdirectoriesUnder(string root)
    {
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        // Deepest paths first, so parents become empty only after their children are removed.
        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
