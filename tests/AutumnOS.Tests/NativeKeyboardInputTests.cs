using System.Text;
using System.Text.Json;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

internal static class NativeKeyboardInputTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.keyboard.finite_keys_and_ascii_mapping", FiniteKeys);
        yield return ("native.keyboard.rejects_ambiguous_or_extended_schema", StrictSchema);
        yield return ("native.keyboard.rejects_non_ascii_and_wrong_physical_key", CharacterBoundary);
        yield return ("native.keyboard.character_consumes_one_held_key_only", CharacterSequence);
        yield return ("native.keyboard.rejects_without_mutating_state", RejectionIsAtomic);
        yield return ("native.keyboard.reset_clears_keys_and_character_allowances", Reset);
    }

    private static NativeKeyboardCommand Parse(string json) => NativeKeyboardInput.Parse(Encoding.UTF8.GetBytes(json));
    private static NativeKeyboardCommand Command(string action, string key, string? text = null) =>
        NativeKeyboardInput.Parse(text is null ? JsonSerializer.SerializeToUtf8Bytes(new { action, key }) : JsonSerializer.SerializeToUtf8Bytes(new { action, key, text }));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("NATIVE_KEYBOARD_BOUNDARY_ASSERTION"); }
    private static void Reject(Action test)
    { try { test(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("NATIVE_KEYBOARD_INVALID_COMMAND_ACCEPTED"); }

    private static void FiniteKeys()
    {
        foreach (char value in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var down = Command("down", "Key" + value);
            Check(down.VirtualKey == value && down.KeyValue == char.ToLowerInvariant(value).ToString());
            Check(Command("char", down.Key, value.ToString()).KeyValue == value.ToString());
            Check(Command("char", down.Key, char.ToLowerInvariant(value).ToString()).Key == down.Key);
        }
        foreach (char value in "0123456789") Check(Command("down", "Digit" + value).VirtualKey == value);
        foreach (var item in new[] { ("ArrowLeft", 37), ("ArrowRight", 39), ("ArrowUp", 38), ("ArrowDown", 40),
            ("Enter", 13), ("Space", 32), ("Escape", 27), ("Tab", 9), ("Backspace", 8), ("Delete", 46), ("Home", 36), ("End", 35) })
            Check(Command("up", item.Item1).VirtualKey == item.Item2);
        Check(Command("char", "Space", " ").KeyValue == " ");
    }
    private static void StrictSchema()
    {
        foreach (string json in new[] { "", "[]", "{}", "{\"action\":\"down\"}",
            "{\"action\":\"down\",\"key\":\"KeyA\",\"key\":\"KeyB\"}",
            "{\"action\":\"down\",\"key\":\"KeyA\",\"text\":\"a\"}",
            "{\"action\":\"char\",\"key\":\"KeyA\"}",
            "{\"action\":\"down\",\"key\":\"KeyA\",\"method\":\"Runtime.evaluate\"}",
            "{\"action\":\"down\",\"key\":1}", "{\"action\":\"down\",\"key\":null}",
            "{\"action\":\"down\",\"key\":{\"nested\":{\"key\":\"KeyA\"}}}",
            "{\"action\":\"Down\",\"key\":\"KeyA\"}", "{\"action\":\"down\",\"key\":\"F12\"}",
            new string(' ', NativeKeyboardInput.MaximumPayloadBytes + 1) }) Reject(() => Parse(json));
        Reject(() => NativeKeyboardInput.Parse([0xff, 0xfe]));
    }
    private static void CharacterBoundary()
    {
        foreach (string text in new[] { "", "ab", "\n", "\0", "中", "é", "\ud800", "!", "a " })
            Reject(() => Command("char", "KeyA", text));
        Reject(() => Command("char", "KeyB", "a"));
        Reject(() => Command("char", "Digit1", "2"));
        Reject(() => Command("char", "Enter", " "));
    }
    private static void CharacterSequence()
    {
        var state = new NativeKeyboardState();
        Check(!state.TryCharacter('a', out _));
        Check(state.TryDown("KeyA") && state.TryDown("KeyB"));
        Check(!state.TryDown("KeyA") && !state.TryCharacter('c', out _));
        Check(state.TryCharacter('B', out string b) && b == "KeyB");
        Check(!state.TryCharacter('b', out _) && state.TryCharacter('a', out string a) && a == "KeyA");
        Check(state.TryUp("KeyA") && !state.TryUp("KeyA"));
        Check(state.TryDown("KeyA") && state.TryCharacter('A', out _));
        Check(state.HeldKeys.Count == 2);
    }
    private static void RejectionIsAtomic()
    {
        var state = new NativeKeyboardState();
        state.Apply(Command("down", "KeyA"));
        Reject(() => state.Apply(default));
        Reject(() => state.Apply(new(null!, "KeyB")));
        Reject(() => state.Apply(new("down", null!)));
        Reject(() => state.Apply(new("char", "KeyB", "a")));
        Reject(() => state.Apply(new("down", "KeyB", "secret")));
        Reject(() => state.Apply(Command("up", "KeyB")));
        Reject(() => state.Apply(Command("down", "KeyA")));
        Check(state.HeldKeys.SequenceEqual(new[] { "KeyA" }));
        state.Apply(Command("char", "KeyA", "A"));
        Reject(() => state.Apply(Command("char", "KeyA", "a")));
        Check(state.HeldKeys.SequenceEqual(new[] { "KeyA" }));
        state.Apply(Command("up", "KeyA"));
        Check(state.HeldKeys.Count == 0);
    }
    private static void Reset()
    {
        var state = new NativeKeyboardState();
        Check(state.TryDown("Space") && state.TryDown("Digit1"));
        var snapshot = state.HeldKeys;
        state.Reset();
        Check(state.HeldKeys.Count == 0 && snapshot.Count == 2 && !state.TryCharacter(' ', out _) && !state.TryUp("Digit1"));
        Reject(() => state.Apply(Command("char", "Digit1", "1")));
        state.Apply(Command("down", "Digit1")); state.Apply(Command("char", "Digit1", "1"));
        state.Apply(Command("up", "Digit1"));
        Check(state.HeldKeys.Count == 0);
    }
}
