using System.Text.RegularExpressions;

namespace Sim.Tests;

/// <summary>
/// Regression guard for the Godot input map (<c>godot/project.godot</c>).
///
/// Why this exists: the layout editor's "delete block" action was bound to
/// keycode 4194322 — <c>KEY_DOWN</c>, not <c>KEY_DELETE</c> (4194312) — for a
/// full delivery cycle. The desktop smoke test injects *action names*, never
/// physical keys, so it kept passing while Delete did nothing and ↓ deleted the
/// selected block on top of its 0.05 m nudge. Only the raw project file shows
/// the mistake, so the raw project file is what this test reads.
///
/// Godot 4 keycodes are <c>KEY_SPECIAL | n</c> with KEY_SPECIAL = 1 &lt;&lt; 22;
/// the values below are checked against the engine's own constants (run a
/// headless GDScript probe printing KEY_DELETE / KEY_DOWN to re-derive them).
/// </summary>
public sealed class GodotInputMapTests
{
    private const int KeySpecial = 1 << 22;
    private const int KeyDelete = KeySpecial | 0x08;  // 4194312
    private const int KeyDown = KeySpecial | 0x12;    // 4194322
    private const int KeyB = 66;
    private const int KeyK = 75;

    /// <summary>Parses `name={ ... "keycode":N ... }` blocks out of the [input] section.</summary>
    private static Dictionary<string, List<int>> ReadInputMap()
    {
        var text = File.ReadAllText(FindRepoFile("godot/project.godot"));
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var action = new Regex(@"^(?<name>[a-z_][a-z0-9_]*)=\{\s*$", RegexOptions.Multiline);
        var keycode = new Regex(@"""keycode"":(?<code>\d+)");
        foreach (Match m in action.Matches(text))
        {
            // The action block runs until the next `name={` line (or EOF).
            var next = action.Match(text, m.Index + m.Length);
            var body = next.Success
                ? text[m.Index..next.Index]
                : text[m.Index..];
            var codes = keycode.Matches(body).Select(k => int.Parse(k.Groups["code"].Value)).ToList();
            map[m.Groups["name"].Value] = codes;
        }
        return map;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void EditorBlockActions_UseTheDocumentedPhysicalKeys()
    {
        var map = ReadInputMap();

        Assert.Equal([KeyB], map["editor_block_add"]);
        Assert.Equal([KeyK], map["editor_block_kind"]);
        Assert.Equal([KeyDelete], map["editor_block_remove"]);
    }

    [Fact]
    public void EditorBlockRemove_IsNotBoundToArrowDown()
    {
        var map = ReadInputMap();

        // 4194322 is KEY_DOWN, which the editor already consumes as `ui_down`
        // to nudge the selected entity by 0.05 m. Binding the delete action to
        // it makes ↓ delete *and* move, and Delete a no-op.
        Assert.DoesNotContain(KeyDown, map["editor_block_remove"]);
        Assert.Equal(KeyDown, KeySpecial | 0x12);
    }

    [Fact]
    public void InputMap_HasNoDuplicateKeycodesBetweenActions()
    {
        var map = ReadInputMap();
        var seen = new Dictionary<int, string>();
        foreach (var (name, codes) in map)
        {
            foreach (var code in codes.Where(c => c != 0)) // 0 = modifier-driven binding (Ctrl+Z/Y)
            {
                Assert.False(
                    seen.TryGetValue(code, out var owner),
                    $"keycode {code} is bound to both '{owner}' and '{name}'");
                seen[code] = name;
            }
        }
    }
}
