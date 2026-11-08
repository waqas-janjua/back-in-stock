using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackInStock.Configuration;

namespace BackInStock.Services;

public sealed record Product(
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("price")] decimal? Price,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("in_stock")] bool InStock);

public sealed class StockClient(HttpClient http, AppSettings cfg)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpRequestMessage Req(string path)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, cfg.StockApiUrl.TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.StockApiKey);
        return r;
    }

    public async Task<List<Product>> CatalogueAsync(CancellationToken ct)
    {
        using var res = await http.SendAsync(Req(""), ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var el = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("products");
        return el.Deserialize<List<Product>>(Json) ?? [];
    }

    /// <summary>Live stock for one SKU. The webhook's quantity is never trusted.</summary>
    public async Task<Product> GetAsync(string sku, CancellationToken ct)
    {
        using var res = await http.SendAsync(Req("/" + Uri.EscapeDataString(sku)), ct);
        res.EnsureSuccessStatusCode();
        var p = await res.Content.ReadFromJsonAsync<Product>(Json, ct)
                ?? throw new InvalidOperationException("Empty stock response");
        return p with { InStock = p.Quantity > 0 && p.InStock };
    }
}
