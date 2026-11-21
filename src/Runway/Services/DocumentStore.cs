using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Runway.Models;

namespace Runway.Services;

/// <summary>
/// Files household documents in a folder, by default in OneDrive, laid out as
/// <c>&lt;Person&gt;/&lt;Year&gt;/&lt;Type&gt;/&lt;yyyy-MM-dd&gt; &lt;Title&gt;.pdf</c> so the folder also makes sense
/// when browsed in Finder or the OneDrive app.
/// </summary>
public sealed class DocumentStore(string rootFolder)
{
    public const string HouseholdFolder = "Household";

    /// <summary>Deleted documents are moved here rather than removed, so nothing is lost by accident.</summary>
    public const string DeletedFolder = "Deleted";

    /// <summary>The documents folder's name, matching the app's name.</summary>
    public const string DefaultFolderName = "KeystoneApp";

    /// <summary>The folder's name from when the app was called Runway.</summary>
    public const string LegacyFolderName = "RunwayApp";

    /// <summary>
    /// OneDrive/PRAS/KeystoneApp. Uses the ~/OneDrive shortcut when there is one, otherwise the
    /// OneDrive folder macOS keeps under Library/CloudStorage.
    /// </summary>
    /// <summary>Set this environment variable to use another folder as OneDrive, e.g. in tests.</summary>
    public const string OneDrivePathVariable = "RUNWAY_ONEDRIVE_PATH";

    public static string DefaultRootFolder()
    {
        if (Environment.GetEnvironmentVariable(OneDrivePathVariable) is { Length: > 0 } custom)
            return Path.Combine(custom, "PRAS", DefaultFolderName);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oneDrive = Path.Combine(home, "OneDrive");
        if (!Directory.Exists(oneDrive))
        {
            try
            {
                oneDrive = Directory.EnumerateDirectories(Path.Combine(home, "Library", "CloudStorage"), "OneDrive*")
                    .Order().FirstOrDefault() ?? oneDrive;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep ~/OneDrive; the user can pick the folder in Settings.
            }
        }

        return Path.Combine(oneDrive, "PRAS", DefaultFolderName);
    }

    /// <summary>The Runway-era folder next to <paramref name="root"/>, e.g. OneDrive/PRAS/RunwayApp.</summary>
    public static string LegacyFolderFor(string root) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) ?? "", LegacyFolderName);

    /// <summary>
    /// Renames the RunwayApp folder to <paramref name="root"/> (KeystoneApp), so documents filed before the
    /// app was renamed move along; their paths are relative, so nothing else changes. Returns the folder to
    /// use: <paramref name="root"/>, or the old folder if it couldn't be renamed (never splitting documents
    /// over two folders).
    /// </summary>
    public static (string Folder, string? Problem) MoveLegacyFolder(string root)
    {
        var legacy = LegacyFolderFor(root);
        if (!Directory.Exists(legacy))
            return (root, null);

        try
        {
            // An empty new folder (e.g. made by OneDrive or by hand) is in the way but holds nothing.
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
            if (Directory.Exists(root))
                return (legacy, $"both “{LegacyFolderName}” and “{DefaultFolderName}” exist and hold files");

            Directory.Move(legacy, root);
            return (root, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (legacy, ex.Message);
        }
    }

    public string RootFolder { get; } = rootFolder;

    public string FullPath(string relativePath) =>
        Path.Combine(RootFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(HouseholdDocument document) =>
        document.RelativePath != "" && File.Exists(FullPath(document.RelativePath));

    /// <summary>Where a document with these details belongs, relative to the root, without a numbered suffix.</summary>
    public static string PlannedPath(HouseholdDocument document, string? personName, string extension)
    {
        var person = SafeName(string.IsNullOrWhiteSpace(personName) ? HouseholdFolder : personName);
        var title = SafeName(string.IsNullOrWhiteSpace(document.Title) ? HouseholdDocument.KindName(document.Kind) : document.Title);
        if (title.Length > 80)
            title = title[..80].TrimEnd();
        var fileName = $"{document.Date:yyyy-MM-dd} {title}{extension.ToLowerInvariant()}";
        return string.Join('/', person, document.Date.Year.ToString(), HouseholdDocument.FolderName(document.Kind), fileName);
    }

    /// <summary>Copies a new file into the folder. Returns its path relative to the root.</summary>
    public string Add(string sourcePath, HouseholdDocument document, string? personName)
    {
        var target = FreePath(PlannedPath(document, personName, Path.GetExtension(sourcePath)), current: null);
        var full = FullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(sourcePath, full);
        return target;
    }

    /// <summary>
    /// Moves the file when its details changed where it should be filed. Returns the new relative
    /// path, or the old one when nothing had to move.
    /// </summary>
    public string Refile(HouseholdDocument document, string? personName)
    {
        var current = document.RelativePath;
        var planned = PlannedPath(document, personName, Path.GetExtension(current));
        if (SamePlace(current, planned) || !File.Exists(FullPath(current)))
            return current;

        var target = FreePath(planned, current);
        var full = FullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Move(FullPath(current), full);
        RemoveEmptyFolders(Path.GetDirectoryName(FullPath(current))!);
        return target;
    }

    /// <summary>Moves the file into the Deleted folder.</summary>
    public void Delete(HouseholdDocument document)
    {
        var full = FullPath(document.RelativePath);
        if (!File.Exists(full))
            return;

        var target = FreePath($"{DeletedFolder}/{Path.GetFileName(full)}", current: null);
        Directory.CreateDirectory(Path.Combine(RootFolder, DeletedFolder));
        File.Move(full, FullPath(target));
        RemoveEmptyFolders(Path.GetDirectoryName(full)!);
    }

    public void Open(HouseholdDocument document) => Run("open", FullPath(document.RelativePath));

    public void RevealInFinder(HouseholdDocument document) => Run("open", "-R", FullPath(document.RelativePath));

    public void OpenRoot()
    {
        Directory.CreateDirectory(RootFolder);
        Run("open", RootFolder);
    }

    /// <summary>Replaces characters that aren't allowed in file names on macOS, Windows or OneDrive.</summary>
    public static string SafeName(string name)
    {
        var invalid = "/\\:*?\"<>|".ToCharArray().Concat(Path.GetInvalidFileNameChars()).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return cleaned == "" ? "Untitled" : cleaned;
    }

    /// <summary>The planned path, or "name (2).pdf" and so on if another file already has it.</summary>
    private string FreePath(string planned, string? current)
    {
        var directory = Path.GetDirectoryName(planned.Replace('/', Path.DirectorySeparatorChar))!.Replace(Path.DirectorySeparatorChar, '/');
        var name = Path.GetFileNameWithoutExtension(planned);
        var extension = Path.GetExtension(planned);
        var candidate = planned;
        for (var i = 2; File.Exists(FullPath(candidate)) &&
                        !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase); i++)
            candidate = $"{directory}/{name} ({i}){extension}";
        return candidate;
    }

    /// <summary>A file already numbered "(2)" in the right folder doesn't need to move.</summary>
    private static bool SamePlace(string path, string? other)
    {
        if (other is null)
            return false;
        if (string.Equals(path, other, StringComparison.OrdinalIgnoreCase))
            return true;

        var withoutNumber = System.Text.RegularExpressions.Regex.Replace(other, @" \(\d+\)(?=\.[^.]*$|$)", "");
        return string.Equals(path, withoutNumber, StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveEmptyFolders(string folder)
    {
        var root = Path.GetFullPath(RootFolder).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(folder);
        while (current.StartsWith(root + Path.DirectorySeparatorChar) && Directory.Exists(current) &&
               !Directory.EnumerateFileSystemEntries(current).Any(e => Path.GetFileName(e) != ".DS_Store"))
        {
            Directory.Delete(current, recursive: true);
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void Run(string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }
}
