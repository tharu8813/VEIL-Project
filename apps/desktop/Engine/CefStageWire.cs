// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;

namespace Veil.Desktop;

// Owner-only staging request/response. A successful staging response is
// not a redemption receipt and does not authorize native initialization.
internal static class CefStageWire
{
    internal static void WriteRequest(Utf8JsonWriter writer, string volumeId, long childHandle)
    {
        if (volumeId.Length is 0 or > 64 ||
            !volumeId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_') ||
            childHandle <= 0)
            throw new ArgumentException("Invalid CEF staging request.");
        writer.WriteStartObject();
        writer.WriteString("command", "StageCefChild");
        writer.WriteString("volumeId", volumeId);
        writer.WriteString("childHandle", childHandle.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    internal static bool IsProtectedStageReply(JsonElement root, string expectedGuid)
    {
        if (string.IsNullOrWhiteSpace(expectedGuid) ||
            !ExactObject(root, "ok", "status") ||
            root.GetProperty("ok").ValueKind != JsonValueKind.True)
            return false;
        var state = root.GetProperty("status");
        if (!ExactObject(state, "attached", "volumeGuid", "fullyEncrypted",
                "protectionOn", "unlocked", "readyToUnlock", "protectorPresent"))
            return false;
        var guid = state.GetProperty("volumeGuid");
        return guid.ValueKind == JsonValueKind.String &&
            string.Equals(guid.GetString(), expectedGuid, StringComparison.OrdinalIgnoreCase) &&
            state.GetProperty("attached").ValueKind == JsonValueKind.True &&
            state.GetProperty("fullyEncrypted").ValueKind == JsonValueKind.True &&
            state.GetProperty("protectionOn").ValueKind == JsonValueKind.True &&
            state.GetProperty("unlocked").ValueKind == JsonValueKind.True &&
            state.GetProperty("protectorPresent").ValueKind == JsonValueKind.True &&
            state.GetProperty("readyToUnlock").ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool ExactObject(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }
}
