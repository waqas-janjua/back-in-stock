namespace BackInStock.Configuration;

public sealed class AppSettings
{
    public string StockApiUrl { get; set; } = "";
    public string StockApiKey { get; set; } = "";
    public string ResendApiKey { get; set; } = "";
    public string ResendBaseUrl { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string AdminPassword { get; set; } = "";
}