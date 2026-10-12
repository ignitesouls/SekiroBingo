using System.Text.Json;

namespace BingoQoL;

public sealed record QolSettings(bool DisableTutorials, bool Borderless)
{
    public static QolSettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new(ReadBoolean(root, "DisableTutorials"), ReadBoolean(root, "Borderless"));
    }

    private static bool ReadBoolean(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"settings.json must contain a true/false value for {name}.");
        return value.GetBoolean();
    }
}
