using System.Runtime.CompilerServices;
using Runway.Localization;
using Runway.Services;

namespace Runway.Tests;

internal static class TestSetup
{
    /// <summary>Tests check English texts and formats, whatever language this Mac uses.</summary>
    [ModuleInitializer]
    internal static void UseEnglish() => Loc.Instance.SetLanguage("en");

    /// <summary>Tests never touch the real OneDrive folder: loading the app renames its documents folder.</summary>
    [ModuleInitializer]
    internal static void UseTemporaryOneDrive() =>
        Environment.SetEnvironmentVariable(DocumentStore.OneDrivePathVariable,
            Path.Combine(Path.GetTempPath(), $"runway-tests-onedrive-{Guid.NewGuid():N}"));
}
