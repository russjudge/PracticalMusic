using System.Text.Json;
using System.Text.Json.Serialization;

namespace PracticalMusic.Core.Models;

public static class BookJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(Book book) => JsonSerializer.Serialize(book, Options);

    public static Book Deserialize(string json) =>
        JsonSerializer.Deserialize<Book>(json, Options) ?? throw new InvalidDataException("book.json is empty");

    public static Book Clone(Book book) => Deserialize(Serialize(book));
}
