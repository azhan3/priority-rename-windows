using System.Text.RegularExpressions;

namespace PriorityRename;

public sealed record RenameMapping(string OriginalPath, string NewPath);
public sealed record RenameResult(bool Success, IReadOnlyList<RenameMapping> Mappings, string? Error = null);

public static class RenameEngine
{
    public static bool IsValidTargetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
            return false;
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return false;

        var stem = name;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            stem = name[..dot];
        stem = stem.TrimEnd(' ');
        if (stem.Length == 0)
            return false;

        if (stem.EndsWith('.') || stem.EndsWith(' '))
            return false;
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase))
            return false;
        return !stem.EndsWith('.') && !stem.EndsWith(' ');
    }

    public static RenameResult Rename(string sourcePath, string targetName, bool takePriority)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            return new RenameResult(false, [], "The selected item no longer exists.");

        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
            return new RenameResult(false, [], "Could not determine the source directory.");

        if (!IsValidTargetName(targetName))
            return new RenameResult(false, [], "The target name is not valid for Windows.");

        var targetPath = Path.Combine(directory, targetName);
        if (PathsEqual(sourcePath, targetPath))
        {
            if (string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
                return new RenameResult(true, []);
            var selfRenameError = MoveItem(sourcePath, targetPath);
            return selfRenameError == null
                ? new RenameResult(true, [new RenameMapping(sourcePath, targetPath)])
                : new RenameResult(false, [], selfRenameError);
        }

        var sourceIsDirectory = Directory.Exists(sourcePath);
        if (sourceIsDirectory && File.Exists(targetPath))
            return new RenameResult(false, [], "A file with that name already exists.");
        if (!sourceIsDirectory && Directory.Exists(targetPath))
            return new RenameResult(false, [], "A folder with that name already exists.");

        var targetExists = sourceIsDirectory ? Directory.Exists(targetPath) : File.Exists(targetPath);
        if (!targetExists)
        {
            var error = MoveItem(sourcePath, targetPath);
            return error == null
                ? new RenameResult(true, [new RenameMapping(sourcePath, targetPath)])
                : new RenameResult(false, [], error);
        }

        if (!takePriority)
        {
            var finalName = FindAvailableName(directory, targetName, sourceIsDirectory);
            if (finalName == null)
                return new RenameResult(false, [], "Could not find an available filename.");
            var finalPath = Path.Combine(directory, finalName);
            var error = MoveItem(sourcePath, finalPath);
            return error == null
                ? new RenameResult(true, [new RenameMapping(sourcePath, finalPath)])
                : new RenameResult(false, [], error);
        }

        return CascadeRename(sourcePath, targetName, sourceIsDirectory);
    }

    public static RenameResult Undo(IReadOnlyList<RenameMapping> mappings)
    {
        if (mappings.Count == 0)
            return new RenameResult(false, [], "There is no rename operation to undo.");

        var items = mappings
            .Where(x => !PathsEqual(x.OriginalPath, x.NewPath))
            .Select(x => (newPath: x.NewPath, originalPath: x.OriginalPath))
            .ToList();

        if (items.Count == 0)
            return new RenameResult(true, []);

        foreach (var item in items)
        {
            var lockError = TryLock(item.newPath);
            if (lockError != null)
                return new RenameResult(false, [], lockError);
        }

        return ExecuteExchange(items.Select(x => (x.newPath, x.originalPath)).ToList());
    }

    private static RenameResult CascadeRename(string sourcePath, string targetName, bool isDirectory)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var (stem, ext) = SplitName(targetName, isDirectory);
        var chain = new SortedDictionary<int, string>();

        foreach (var path in isDirectory
                     ? Directory.GetDirectories(directory)
                     : Directory.GetFiles(directory))
        {
            if (PathsEqual(path, sourcePath))
                continue;

            var name = Path.GetFileName(path);
            if (!TryMatchChainName(name, stem, ext, isDirectory, out var index))
                continue;
            chain[index] = path;
        }

        var contiguous = new SortedDictionary<int, string>();
        for (var index = 1; ; index++)
        {
            if (!chain.TryGetValue(index, out var path))
                break;
            contiguous[index] = path;
        }

        var lockedPaths = contiguous.Values.Append(sourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in lockedPaths)
        {
            var lockError = TryLock(path);
            if (lockError != null)
                return new RenameResult(false, [], lockError);
        }

        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guid = Guid.NewGuid().ToString("N");
        var counter = 0;
        foreach (var path in lockedPaths)
        {
            var tempName = $".__tmp_{guid}_{++counter}_{Path.GetFileName(path)}";
            var tempPath = Path.Combine(directory, tempName);
            try
            {
                MoveItemUnchecked(path, tempPath);
                staged[path] = tempPath;
            }
            catch (Exception ex)
            {
                RevertStaged(staged);
                return new RenameResult(false, [], $"Rename could not be staged: {ex.Message}");
            }
        }

        var targetPath = Path.Combine(directory, targetName);
        var destinations = new List<(string stagedPath, string destinationPath)>
        {
            (staged[sourcePath], targetPath)
        };
        foreach (var item in contiguous)
        {
            var destinationName = indexName(stem, ext, item.Key + 1);
            destinations.Add((staged[item.Value], Path.Combine(directory, destinationName)));
        }

        foreach (var item in destinations)
        {
            try
            {
                MoveItemUnchecked(item.stagedPath, item.destinationPath);
            }
            catch (Exception ex)
            {
                RollBackAfterCommit(staged, destinations);
                return new RenameResult(false, [], $"Rename failed: {ex.Message}");
            }
        }

        NotifyShell(directory);
        var mappings = destinations.Select(x =>
        {
            var original = staged.First(y => PathsEqual(y.Value, x.stagedPath)).Key;
            return new RenameMapping(original, x.destinationPath);
        }).ToList();
        return new RenameResult(true, mappings);
    }

    private static RenameResult ExecuteExchange(List<(string source, string destination)> items)
    {
        var directory = Path.GetDirectoryName(items[0].source)!;
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guid = Guid.NewGuid().ToString("N");
        var counter = 0;

        foreach (var item in items)
        {
            var tempPath = Path.Combine(directory, $".__tmp_{guid}_{++counter}_{Path.GetFileName(item.source)}");
            try
            {
                MoveItemUnchecked(item.source, tempPath);
                staged[item.source] = tempPath;
            }
            catch (Exception ex)
            {
                RevertStaged(staged);
                return new RenameResult(false, [], $"Undo could not be staged: {ex.Message}");
            }
        }

        foreach (var item in items)
        {
            try
            {
                MoveItemUnchecked(staged[item.source], item.destination);
            }
            catch (Exception ex)
            {
                RollBackAfterCommit(staged, items.Select(x => (staged[x.source], x.destination)).ToList());
                return new RenameResult(false, [], $"Undo failed: {ex.Message}");
            }
        }

        NotifyShell(directory);
        return new RenameResult(true, items.Select(x => new RenameMapping(x.destination, x.source)).ToList());
    }

    private static (string stem, string ext) SplitName(string name, bool isDirectory)
    {
        if (isDirectory)
            return (name, "");
        var dot = name.LastIndexOf('.');
        return dot > 0
            ? (name[..dot], name[dot..])
            : (name, "");
    }

    private static bool TryMatchChainName(string name, string stem, string ext, bool isDirectory, out int index)
    {
        index = 0;
        if (isDirectory)
        {
            if (string.Equals(name, stem, StringComparison.OrdinalIgnoreCase))
            {
                index = 1;
                return true;
            }
            var match = Regex.Match(name, $"^{Regex.Escape(stem)} \\((\\d+)\\)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                index = int.Parse(match.Groups[1].Value);
                return index >= 2;
            }
            return false;
        }

        var exact = stem + ext;
        if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
        {
            index = 1;
            return true;
        }
        var suffixPattern = $"^{Regex.Escape(stem)} \\((\\d+)\\){Regex.Escape(ext)}$";
        var suffixMatch = Regex.Match(name, suffixPattern, RegexOptions.IgnoreCase);
        if (!suffixMatch.Success)
            return false;
        index = int.Parse(suffixMatch.Groups[1].Value);
        return index >= 2;
    }

    private static string indexName(string stem, string ext, int index)
        => index == 1 ? stem + ext : $"{stem} ({index}){ext}";

    private static string? FindAvailableName(string directory, string targetName, bool isDirectory)
    {
        var (stem, ext) = SplitName(targetName, isDirectory);
        for (var index = 2; index < 100000; index++)
        {
            var name = indexNameForAvailable(stem, ext, index);
            var path = Path.Combine(directory, name);
            var exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
                return name;
        }
        return null;
    }

    private static string indexNameForAvailable(string stem, string ext, int index)
        => $"{stem} ({index}){ext}";

    private static string? TryLock(string path)
    {
        try
        {
            using var stream = (File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : null);
            return null;
        }
        catch (IOException)
        {
            return $"'{Path.GetFileName(path)}' is open or locked by another process.";
        }
        catch (UnauthorizedAccessException)
        {
            return $"You do not have permission to modify '{Path.GetFileName(path)}'.";
        }
    }

    private static string? MoveItem(string source, string destination)
    {
        try
        {
            MoveItemUnchecked(source, destination);
            NotifyShell(Path.GetDirectoryName(source)!);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void MoveItemUnchecked(string source, string destination)
    {
        if (File.Exists(source))
            File.Move(source, destination);
        else
            Directory.Move(source, destination);
    }

    private static void RevertStaged(Dictionary<string, string> staged)
    {
        foreach (var item in staged.Reverse())
        {
            try
            {
                if (File.Exists(item.Value) || Directory.Exists(item.Value))
                    MoveItemUnchecked(item.Value, item.Key);
            }
            catch
            {
            }
        }
    }

    private static void RollBackAfterCommit(
        Dictionary<string, string> staged,
        List<(string stagedPath, string destinationPath)> completedOrAttempted)
    {
        foreach (var item in completedOrAttempted)
        {
            try
            {
                if (File.Exists(item.destinationPath) || Directory.Exists(item.destinationPath))
                    MoveItemUnchecked(item.destinationPath, item.stagedPath);
            }
            catch
            {
            }
        }
        RevertStaged(staged);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, int flags, nint item1, nint item2);

    private static void NotifyShell(string directory)
    {
        var ptr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(directory);
        try
        {
            SHChangeNotify(0x00000800, 0x0000, ptr, nint.Zero);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }
    }
}
