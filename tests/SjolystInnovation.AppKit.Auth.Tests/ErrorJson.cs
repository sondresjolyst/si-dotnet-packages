using System.Text.Json;
using Xunit;

namespace SjolystInnovation.AppKit.Auth.Tests;

/// <summary>Reads the error bodies of the endpoints: the code of a plain error, and the codes and limits of a validation problem.</summary>
internal static class ErrorJson
{
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetString()!;

    /// <summary>The codes by field, as <c>Field=code1,code2</c> entries in field order.</summary>
    public static async Task<string[]> FieldCodesAsync(HttpResponseMessage response)
    {
        var json = await ReadAsync(response);
        var errors = json.GetProperty("errors");
        var codes = json.GetProperty("codes");
        // Every message has a code at the same place, and every code a message.
        Assert.Equal(errors.EnumerateObject().Select(p => p.Name).Order(), codes.EnumerateObject().Select(p => p.Name).Order());
        foreach (var field in errors.EnumerateObject())
            Assert.Equal(field.Value.GetArrayLength(), codes.GetProperty(field.Name).GetArrayLength());
        return codes.EnumerateObject()
            .Select(p => $"{p.Name}={string.Join(',', p.Value.EnumerateArray().Select(c => c.GetString()))}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The limits by field, as <c>Field=number</c> entries in field order.</summary>
    public static async Task<string[]> LimitsAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("limits").EnumerateObject()
            .Select(p => $"{p.Name}={p.Value.GetInt32()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The English messages of one field.</summary>
    public static async Task<string[]> MessagesAsync(HttpResponseMessage response, string field) =>
        (await ReadAsync(response)).GetProperty("errors").GetProperty(field).EnumerateArray().Select(m => m.GetString()!).ToArray();
}
