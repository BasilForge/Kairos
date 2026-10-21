using System.Reflection;

namespace Kairos.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public static class Assert
{
    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what ?? "value"}: expected <{expected}> but was <{actual}>");
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    public static void Near(double expected, double actual, double tolerance, string? what = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception($"{what ?? "value"}: expected {expected}±{tolerance} but was {actual}");
    }
}

/// <summary>The Windows project's runner (same output), plus a list of tests that cannot run on Linux, each with its reason.</summary>
public static class Program
{
    const string Keys = "uses F18-F23, which a PC keyboard map lacks (X11 cannot grab an unmapped key); LinuxHotkeyTests covers the same";
    const string Render = "draws with Avalonia, which needs the running app (the editor export was checked in the UI run)";
    const string Drive = "uses a Windows drive-letter path";

    static readonly Dictionary<string, string> Skipped = new()
    {
        ["HotkeyTests.A_key_is_free_only_when_no_app_holds_it"] = Keys,
        ["HotkeyTests.A_key_saved_while_typing_is_taken_when_typing_ends"] = Keys,
        ["HotkeyTests.A_key_that_was_let_go_is_not_retried"] = Keys,
        ["HotkeyTests.A_taken_key_is_picked_up_once_the_other_app_lets_go"] = Keys,
        ["HotkeyTests.Combo_taken_by_another_owner_reports_failure"] = Keys,
        ["HotkeyTests.Typing_a_key_releases_kairos_keys_until_done"] = Keys,
        ["HotkeyTests.Kairos_own_window_is_told_apart_from_a_game"] = "compares Windows exe paths case-insensitively; Linux paths are case-sensitive",
        ["SettingsTests.Save_then_load_round_trips"] = Drive,
        ["ClipTests.Target_path_uses_app_folder_and_timestamp"] = Drive,
        ["SharePlannerTests.The_game_name_comes_from_the_clip_file_name"] = Drive,
        ["ClipDeleteTests.Missing_or_locked_files_are_skipped_not_fatal"] = "a file in use cannot be locked against deletion on Linux",
        ["EditExportTests.A_text_item_is_in_the_picture"] = Render,
        ["EditExportTests.Fifty_caption_items_export"] = Render,
        ["FrameGrabberTests.The_graphics_card_is_let_go_once_no_reader_uses_it"] = "D3D11 hardware decoding is Windows-only",
        ["ShareSettingsTests.Old_prepared_files_are_cleaned_but_recent_ones_stay"] = "uses a Windows drive-letter path",
    };

    public static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
            .Where(m => filter == null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();

        int failed = 0, skipped = 0;
        foreach (var test in tests)
        {
            string name = $"{test.DeclaringType!.Name}.{test.Name}";
            if (Skipped.TryGetValue(name, out string? reason))
            {
                skipped++;
                Console.WriteLine($"SKIP {name}: {reason}");
                continue;
            }
            try
            {
                test.Invoke(null, null);
                Console.WriteLine($"PASS {name}");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {ex.InnerException?.Message}");
                if (Environment.GetEnvironmentVariable("KAIROS_TEST_TRACE") == "1")
                    Console.WriteLine(ex.InnerException);
            }
        }

        Console.WriteLine($"{tests.Count - failed - skipped}/{tests.Count - skipped} passed, {skipped} skipped");
        return failed;
    }
}
