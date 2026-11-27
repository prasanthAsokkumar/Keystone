using System.Runtime.CompilerServices;
using Keystone.Localization;
using Keystone.Services;

namespace Keystone.Tests;

internal static class TestSetup
{
    /// <summary>Tests check English texts and formats, whatever language this Mac uses.</summary>
    [ModuleInitializer]
    internal static void UseEnglish() => Loc.Instance.SetLanguage("en");

    /// <summary>
    /// Tests never touch the real documents or OneDrive folders: loading the app creates its documents folder and
    /// may rename an earlier one, so the home folder they're looked for in is a temporary one.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTemporaryHome() =>
        Environment.SetEnvironmentVariable(DocumentStore.HomeVariable,
            Path.Combine(Path.GetTempPath(), $"keystone-tests-home-{Guid.NewGuid():N}"));
}
