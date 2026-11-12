using System.Text.Json;

namespace AutumnOS.NativeHost.Windows;

public readonly record struct NativeKeyboardCommand(string Action, string Key, string? Text = null)
{
    public int VirtualKey => NativeKeyboardInput.VirtualKey(Key);
    public string KeyValue => Action == "char" && Text is not null ? Text :
        Key is { Length: 4 } && Key.StartsWith("Key", StringComparison.Ordinal) ? char.ToLowerInvariant(Key[3]).ToString() :
        Key is { Length: 6 } && Key.StartsWith("Digit", StringComparison.Ordinal) ? Key[5].ToString() : Key == "Space" ? " " : Key;
}

// Only finite keyboard operations enter the native browser. Text comes from a
// single native CharacterReceived event, not a TextBox buffer or clipboard.
public static class NativeKeyboardInput
{
    public const int MaximumPayloadBytes = 256;

    public static NativeKeyboardCommand Parse(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is < 1 or > MaximumPayloadBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 2 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            string? action = null, key = null, text = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String) throw Invalid();
                string value = property.Value.GetString()!;
                switch (property.Name)
                {
                    case "action": action = value; break;
                    case "key": key = value; break;
                    case "text": text = value; break;
                    default: throw Invalid();
                }
            }
            if (action is null || key is null || seen.Count != (action == "char" ? 3 : 2)) throw Invalid();
            var command = new NativeKeyboardCommand(action, key, text);
            Validate(command);
            return command;
        }
        catch (JsonException) { throw Invalid(); }
    }

    public static bool IsAllowedKey(string? key) => key is not null &&
        (key.Length == 4 && key.StartsWith("Key", StringComparison.Ordinal) && key[3] is >= 'A' and <= 'Z' ||
         key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal) && key[5] is >= '0' and <= '9' ||
         key is "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" or "Enter" or "Space" or "Escape" or "Tab" or
             "Backspace" or "Delete" or "Home" or "End");

    internal static void Validate(NativeKeyboardCommand command)
    {
        if (!IsAllowedKey(command.Key) || command.Action is not ("down" or "up" or "char")) throw Invalid();
        if (command.Action == "char")
        {
            if (command.Text is not { Length: 1 } || CharacterKey(command.Text[0]) != command.Key) throw Invalid();
        }
        else if (command.Text is not null) throw Invalid();
    }

    internal static string? CharacterKey(char value) => value switch
    {
        >= 'a' and <= 'z' => "Key" + char.ToUpperInvariant(value),
        >= 'A' and <= 'Z' => "Key" + value,
        >= '0' and <= '9' => "Digit" + value,
        ' ' => "Space", _ => null
    };

    internal static int VirtualKey(string? key)
    {
        if (!IsAllowedKey(key)) throw Invalid();
        if (key!.StartsWith("Key", StringComparison.Ordinal)) return key[3];
        if (key.StartsWith("Digit", StringComparison.Ordinal)) return key[5];
        return key switch
        {
            "ArrowLeft" => 37, "ArrowRight" => 39, "ArrowUp" => 38, "ArrowDown" => 40,
            "Enter" => 13, "Space" => 32, "Escape" => 27, "Tab" => 9, "Backspace" => 8,
            "Delete" => 46, "Home" => 36, "End" => 35, _ => throw Invalid()
        };
    }
    private static InvalidDataException Invalid() => new("BROWSER_KEY_COMMAND_INVALID");
}

// Used independently at the native input source and the receiver. A character
// consumes one still-held key's allowance. Neither parsing nor rejection changes
// state, and resetting focus/foreground removes every outstanding allowance.
public sealed class NativeKeyboardState
{
    private readonly Dictionary<string, bool> held = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> HeldKeys => held.Keys.ToArray();
    public bool TryDown(string key) => NativeKeyboardInput.IsAllowedKey(key) && held.TryAdd(key, false);
    public bool TryUp(string key) => NativeKeyboardInput.IsAllowedKey(key) && held.Remove(key);
    public bool TryCharacter(char character, out string key)
    {
        key = "";
        string? candidate = NativeKeyboardInput.CharacterKey(character);
        if (candidate is null || !held.TryGetValue(candidate, out bool consumed) || consumed) return false;
        held[candidate] = true;
        key = candidate;
        return true;
    }
    public void Apply(NativeKeyboardCommand command)
    {
        NativeKeyboardInput.Validate(command);
        bool accepted = command.Action switch
        {
            "down" => TryDown(command.Key), "up" => TryUp(command.Key),
            "char" => TryCharacter(command.Text![0], out _), _ => false
        };
        if (!accepted) throw new InvalidDataException("BROWSER_KEY_SEQUENCE");
    }
    public void Reset() => held.Clear();
}
