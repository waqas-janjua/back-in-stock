using System.Net;
using System.Net.Http.Headers;
using BackInStock.Configuration;
using BackInStock.Data;

namespace BackInStock.Services;

public sealed class Mailer(HttpClient http, AppSettings cfg)
{
    public const string From = "Back in Stock <alerts@backinstock.example>";

    public async Task SendAsync(NotifyRequest r, Product p, CancellationToken ct)
    {
        var name = p.Name ?? p.Sku;
        var req = new HttpRequestMessage(HttpMethod.Post, cfg.ResendBaseUrl.TrimEnd('/') + "/emails")
        {
            Content = JsonContent.Create(new
            {
                from = From,
                to = new[] { r.Email },
                subject = $"{name} is back in stock",
                html = EmailTemplate.Render(p),
                text = $"Good news! {name} is back in stock. Grab it before it sells out again."
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ResendApiKey);
        // Stable per request: a retry after a crash can't produce a second email at providers that honour it.
        req.Headers.TryAddWithoutValidation("Idempotency-Key", "bis-" + r.Id);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Email API {(int)res.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
        }
    }
}

public static class EmailTemplate
{
    public static string Render(Product p)
    {
        var name = WebUtility.HtmlEncode(p.Name ?? p.Sku);
        var sku = WebUtility.HtmlEncode(p.Sku);
        var sym = p.Currency == "GBP" ? "£" : p.Currency == "USD" ? "$" : p.Currency == "EUR" ? "€" : (p.Currency ?? "") + " ";
        var price = p.Price is null ? "" : WebUtility.HtmlEncode(sym) + p.Price.Value.ToString("0.##");
        var img = string.IsNullOrEmpty(p.Image) ? "" :
            $"<img src=\"{WebUtility.HtmlEncode(p.Image)}\" width=\"520\" alt=\"{name}\" style=\"display:block;width:100%;max-width:520px;height:auto;border:0\">";
        const string F = "-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif";
        return $"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light"><title>{name} is back</title></head>
<body style="margin:0;padding:0;background:#f4f1ec;font-family:{F}">
<div style="display:none;max-height:0;overflow:hidden;opacity:0">{name} is available again, but stock is limited.</div>
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f4f1ec;padding:24px 12px"><tr><td align="center">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#ffffff;border-radius:12px;overflow:hidden">
<tr><td>{img}</td></tr>
<tr><td style="padding:28px 28px 8px">
<p style="margin:0 0 8px;font-size:12px;letter-spacing:.12em;text-transform:uppercase;color:#2f7d5b;font-weight:700">Back in stock</p>
<h1 style="margin:0 0 12px;font-size:24px;line-height:1.25;color:#1d1b18">{name} is available again</h1>
<p style="margin:0 0 20px;font-size:16px;line-height:1.5;color:#4a463f">You asked us to let you know. It's here now, but popular items go quickly, so don't wait too long.</p>
<p style="margin:0 0 24px;font-size:18px;color:#1d1b18"><strong>{price}</strong></p>
<table role="presentation" cellpadding="0" cellspacing="0"><tr><td style="background:#1d1b18;border-radius:8px">
<a href="#" style="display:inline-block;padding:14px 28px;font-size:16px;font-weight:600;color:#ffffff;text-decoration:none">Shop now</a></td></tr></table>
</td></tr>
<tr><td style="padding:24px 28px 28px"><p style="margin:0;font-size:12px;line-height:1.5;color:#8a857b">You're receiving this one-time email because you requested a restock alert for {sku}. We won't email you about it again.</p></td></tr>
</table></td></tr></table></body></html>
""";
    }
}
