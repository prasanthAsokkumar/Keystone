using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Keystone.Models;

namespace Keystone.Services;

/// <summary>
/// Files household documents in a folder (by default ~/Documents/Keystone; often one in OneDrive or iCloud Drive), laid out as
/// <c>&lt;Person&gt;/&lt;Year&gt;/&lt;Type&gt;/&lt;yyyy-MM-dd&gt; &lt;Title&gt;.pdf</c> so the folder also makes sense
/// when browsed in Finder or the OneDrive app.
/// </summary>
public sealed class DocumentStore(string rootFolder)
{
    public const string HouseholdFolder = "Household";

    /// <summary>Deleted documents are moved here rather than removed, so nothing is lost by accident.</summary>
    public const string DeletedFolder = "Deleted";

    /// <summary>The documents folder's name: Documents/Keystone in the home folder.</summary>
    public const string DefaultFolderName = "Keystone";

    /// <summary>
    /// The folder's name in earlier versions, which kept documents in a folder inside OneDrive
    /// (OneDrive/&lt;a folder&gt;/KeystoneApp) without saving that choice.
    /// </summary>
    public const string EarlierFolderName = "KeystoneApp";

    /// <summary>The folder's name from when the app was called Runway.</summary>
    public const string LegacyFolderName = "RunwayApp";

    /// <summary>Set this environment variable to use another folder as the home folder, e.g. in tests.</summary>
    public const string HomeVariable = "KEYSTONE_HOME";

    private static string Home() =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } custom
            ? custom
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where documents go unless the user picks a folder: ~/Documents/Keystone.</summary>
    public static string DefaultRootFolder() => Path.Combine(Home(), "Documents", DefaultFolderName);

    /// <summary>
    /// A documents folder an earlier version used by default (OneDrive/&lt;a folder&gt;/KeystoneApp, or RunwayApp
    /// before the rename), so an existing household keeps its folder; null when there is none.
    /// </summary>
    public static string? FindEarlierFolder()
    {
        var home = Home();
        var roots = new List<string> { Path.Combine(home, "OneDrive") };
        try
        {
            roots.AddRange(Directory.EnumerateDirectories(Path.Combine(home, "Library", "CloudStorage"), "OneDrive*").Order());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No cloud folders: only ~/OneDrive is looked at.
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(root).Order().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var folder in folders)
                foreach (var name in new[] { EarlierFolderName, LegacyFolderName })
                    if (Directory.Exists(Path.Combine(folder, name)))
                        return Path.Combine(folder, name);
        }
        return null;
    }

    /// <summary>The Runway-era folder next to <paramref name="root"/>: RunwayApp next to KeystoneApp.</summary>
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
                return (legacy, $"both “{LegacyFolderName}” and “{EarlierFolderName}” exist and hold files");

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

    /// <summary>Moves any file, e.g. an inbox scan already filed, into the Deleted folder.</summary>
    public void Discard(string path)
    {
        if (!File.Exists(path))
            return;
        var target = FreePath($"{DeletedFolder}/{Path.GetFileName(path)}", current: null);
        Directory.CreateDirectory(Path.Combine(RootFolder, DeletedFolder));
        File.Move(path, FullPath(target));
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
