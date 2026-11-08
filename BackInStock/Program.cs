using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackInStock.Configuration;
using BackInStock.Data;
using BackInStock.Services;
using Microsoft.EntityFrameworkCore;

LoadEnvFile(Environment.GetEnvironmentVariable("ENV_FILE") ?? ".env.local");

string[] required = ["STOCK_API_URL", "STOCK_API_KEY", "RESEND_API_KEY", "RESEND_BASE_URL", "WEBHOOK_SECRET", "ADMIN_PASSWORD"];
var missing = required.Where(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"Cannot start: missing required environment variable(s): {string.Join(", ", missing)}");
    Console.Error.WriteLine("Pass them with -e NAME=value or --env-file <file> (see README).");
    Environment.Exit(1);
}

var builder = WebApplication.CreateBuilder(args);

var settings = new AppSettings
{
    StockApiUrl = Env("STOCK_API_URL"),
    StockApiKey = Env("STOCK_API_KEY"),
    ResendApiKey = Env("RESEND_API_KEY"),
    ResendBaseUrl = Env("RESEND_BASE_URL"),
    WebhookSecret = Env("WEBHOOK_SECRET"),
    AdminPassword = Env("ADMIN_PASSWORD"),
};
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port)) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "data/backinstock.db";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

builder.Services.AddSingleton(settings);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath};Default Timeout=30"));
builder.Services.AddHttpClient<StockClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<Mailer>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<CatalogueCache>();
builder.Services.AddHostedService<SweeperService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

var skuRx = new Regex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

app.MapGet("/", () => Results.Redirect("/embed"));
app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

// ---- Embeddable form (meant for an iframe on another site) ----
app.MapGet("/embed", async (HttpContext ctx) =>
{
    ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors *";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "embed.html"));
});

app.MapGet("/api/products", async (CatalogueCache cache, CancellationToken ct) =>
{
    try
    {
        var list = await cache.GetAsync(ct);
        return Results.Ok(list.Select(p => new { sku = p.Sku, name = p.Name ?? p.Sku, in_stock = p.InStock }));
    }
    catch { return Err(503, "Catalogue unavailable"); }
});

// ---- Create a request ----
app.MapPost("/api/requests", async (HttpContext ctx, AppDb db, CatalogueCache cache, StockClient stock, CancellationToken ct) =>
{
    JsonElement body;
    try
    {
        if (ctx.Request.ContentLength > 8192) return Err(413, "Request too large");
        body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
    }
    catch { return Err(400, "Body must be JSON: { \"email\", \"sku\" }"); }
    if (body.ValueKind != JsonValueKind.Object) return Err(400, "Body must be a JSON object");

    var email = Str(body, "email")?.Trim().ToLowerInvariant();
    var sku = Str(body, "sku")?.Trim();
    if (!ValidEmail(email)) return Err(400, "Please enter a valid email address");
    if (sku is null || !skuRx.IsMatch(sku)) return Err(400, "Please choose a product");

    Product? product;
    try
    {
        var list = await cache.GetAsync(ct);
        product = list.FirstOrDefault(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase));
        if (product is null) return Err(404, "We couldn't find that product");
        sku = product.Sku;
        product = await stock.GetAsync(sku, ct);
    }
    catch (OperationCanceledException) { throw; }
    catch { return Err(503, "We couldn't check stock right now. Please try again shortly."); }

    if (product.InStock)
        return Results.Ok(new { status = "in_stock", message = "Good news, this item is in stock right now." });

    var req = new NotifyRequest { Email = email!, Sku = sku, ProductName = product.Name ?? sku };
    try
    {
        db.Requests.Add(req);
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException)
    {
        // Unique index on open (email, sku): treat as idempotent success.
        return Results.Ok(new { status = "already_requested", message = "You're already on the list for this item." });
    }
    return Results.Created($"/api/requests/{req.Id}", new { status = "created", id = req.Id, message = "You're on the list. We'll email you once." });
});

// ---- Stock webhook ----
app.MapPost("/webhooks/stock", async (HttpContext ctx, AppDb db, Notifier notifier, AppSettings cfg, CancellationToken ct) =>
{
    var given = ctx.Request.Headers["X-Webhook-Secret"].ToString();
    if (!SafeEquals(given, cfg.WebhookSecret)) return Err(401, "Invalid webhook secret");

    JsonElement body;
    try
    {
        if (ctx.Request.ContentLength > 65536) return Err(413, "Payload too large");
        using var ms = new MemoryStream();
        var buf = new byte[8192]; int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 65536) return Err(413, "Payload too large");
        }
        body = JsonSerializer.Deserialize<JsonElement>(ms.ToArray());
    }
    catch (OperationCanceledException) { throw; }
    catch { return Err(400, "Invalid JSON"); }
    if (body.ValueKind != JsonValueKind.Object) return Err(400, "Body must be a JSON object");

    var eventId = Str(body, "event_id");
    var sku = Str(body, "sku");
    if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 200) return Err(400, "event_id required");
    if (sku is null || !skuRx.IsMatch(sku)) return Err(400, "sku invalid");
    var qty = body.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var qi) ? qi : 0;

    // Record the delivery; a repeat of the same event_id just bumps a counter.
    var now = DateTime.UtcNow;
    await db.Database.ExecuteSqlInterpolatedAsync(
        $"INSERT INTO Events (EventId, Sku, Quantity, ReceivedAt, Deliveries) VALUES ({eventId}, {sku}, {qty}, {now}, 1) ON CONFLICT(EventId) DO UPDATE SET Deliveries = Deliveries + 1", ct);

    // Processing is idempotent (atomic per-request claims), so duplicate or concurrent
    // deliveries are safe and a redelivery after a failure finishes the job.
    try
    {
        var r = await notifier.ProcessSkuAsync(sku, ct);
        if (r.Failed > 0)
            return Results.Json(new { status = "partial", sent = r.Sent, failed = r.Failed }, statusCode: 503);
        return Results.Ok(new { status = r.InStock ? "processed" : "ignored_not_in_stock", sent = r.Sent });
    }
    catch (OperationCanceledException) { throw; }
    catch { return Err(503, "Stock check failed; retry later"); }
});

// ---- Admin (HTTP Basic, any username, password = ADMIN_PASSWORD) ----
app.MapGet("/admin", async (HttpContext ctx, AppDb db, AppSettings cfg, CancellationToken ct) =>
{
    if (!AdminAuthorized(ctx, cfg))
    {
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Back in Stock admin\", charset=\"UTF-8\"";
        return Results.Text("Authentication required", statusCode: 401);
    }
    var status = ctx.Request.Query["status"].ToString();
    var search = ctx.Request.Query["q"].ToString().Trim().ToLowerInvariant();
    var all = db.Requests.AsNoTracking();
    var counts = await all.GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
    var filtered = all;
    if (status is RequestStatus.Pending or RequestStatus.Sending or RequestStatus.Sent) filtered = filtered.Where(r => r.Status == status);
    else status = "";
    if (search.Length > 0) filtered = filtered.Where(r => r.Email.Contains(search) || r.Sku.ToLower().Contains(search));
    var rows = await filtered.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
    var events = await db.Events.AsNoTracking().CountAsync(ct);
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Content(AdminPage.Render(rows, counts.ToDictionary(c => c.Key, c => c.N), status, search, events), "text/html; charset=utf-8");
});

app.Run();

// ---------- helpers ----------
static string Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : throw new InvalidOperationException($"Missing required environment variable {name}");

static void LoadEnvFile(string path)
{
    if (!File.Exists(path)) return;
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') continue;
        if (line.StartsWith("export ")) line = line[7..];
        var i = line.IndexOf('=');
        if (i <= 0) continue;
        var key = line[..i].Trim();
        var val = line[(i + 1)..].Trim();
        if (val.Length >= 2 && (val[0] == '"' || val[0] == '\'') && val[^1] == val[0]) val = val[1..^1];
        if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, val);
    }
}

static IResult Err(int code, string message) => Results.Json(new { error = message }, statusCode: code);

static string? Str(JsonElement o, string name) =>
    o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

static bool ValidEmail(string? e)
{
    if (string.IsNullOrEmpty(e) || e.Length > 254) return false;
    try { var a = new MailAddress(e); return a.Address == e && e.Split('@')[1].Contains('.'); }
    catch { return false; }
}

static bool SafeEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

static bool AdminAuthorized(HttpContext ctx, AppSettings cfg)
{
    var h = ctx.Request.Headers.Authorization.ToString();
    if (!h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..].Trim()));
        var pw = decoded[(decoded.IndexOf(':') + 1)..];
        return SafeEquals(pw, cfg.AdminPassword);
    }
    catch { return false; }
}

public sealed class CatalogueCache(StockClient stock)
{
    private List<Product> _items = [];
    private DateTime _at = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<Product>> GetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _at < TimeSpan.FromSeconds(30)) return _items;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _at < TimeSpan.FromSeconds(30)) return _items;
            _items = await stock.CatalogueAsync(ct);
            _at = DateTime.UtcNow;
            return _items;
        }
        finally { _lock.Release(); }
    }
}

public static class AdminPage
{
    public static string Render(List<NotifyRequest> rows, Dictionary<string, int> counts, string status, string search, int events)
    {
        string H(string s) => WebUtility.HtmlEncode(s);
        int C(string k) => counts.GetValueOrDefault(k);
        string Time(DateTime? d) => d is null ? "<span class=mute>-</span>"
            : $"<time datetime=\"{d:yyyy-MM-ddTHH:mm:ss}Z\">{d:yyyy-MM-dd HH:mm:ss} UTC</time>";
        string Tab(string key, string label, int n) =>
            $"<a class=\"tab{(status == key ? " on" : "")}\" href=\"/admin?status={key}&amp;q={Uri.EscapeDataString(search)}\">{label} <b>{n}</b></a>";
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            var err = string.IsNullOrEmpty(r.LastError) ? "" : $"<div class=err title=\"{H(r.LastError)}\">{H(r.LastError)}</div>";
            sb.Append($"<tr><td>{H(r.Email)}</td><td><div>{H(r.ProductName)}</div><code>{H(r.Sku)}</code></td>" +
                      $"<td><span class=\"pill {r.Status}\">{r.Status}</span>{err}</td><td>{Time(r.CreatedAt)}</td><td>{Time(r.NotifiedAt)}</td><td class=num>{r.Attempts}</td></tr>");
        }
        if (rows.Count == 0) sb.Append("<tr><td colspan=6 class=empty>No requests yet.</td></tr>");
        var total = C("pending") + C("sending") + C("sent");
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex"><title>Back in Stock admin</title>
<style>
:root{--bg:#f6f4f0;--card:#fff;--ink:#1d1b18;--mute:#7a756b;--line:#e8e3da;--accent:#1d1b18;--ok:#1f7a55;--okbg:#e3f3ec;--warn:#9a6a00;--warnbg:#fbf0d4;--info:#2b5cab;--infobg:#e4edfa}
@media(prefers-color-scheme:dark){:root{--bg:#141311;--card:#1d1b18;--ink:#f1eee8;--mute:#9a948a;--line:#2e2b26;--accent:#f1eee8;--ok:#58c79a;--okbg:#16322a;--warn:#e5b94a;--warnbg:#3a2e12;--info:#7fa8ee;--infobg:#17263f} }
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.45 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif}
.wrap{max-width:1100px;margin:0 auto;padding:28px 16px 60px}
h1{font-size:22px;margin:0 0 4px}.sub{color:var(--mute);margin:0 0 22px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:20px}
.stat{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}.stat span{display:block;color:var(--mute);font-size:12px;text-transform:uppercase;letter-spacing:.06em}.stat b{font-size:26px}
.bar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;justify-content:space-between;margin-bottom:12px}
.tabs{display:flex;gap:6px;flex-wrap:wrap}.tab{padding:7px 12px;border-radius:99px;border:1px solid var(--line);background:var(--card);color:var(--ink);text-decoration:none;font-size:14px}.tab.on{background:var(--accent);color:var(--bg);border-color:var(--accent)}.tab b{font-weight:600;opacity:.7}
form{display:flex;gap:8px}input{padding:8px 12px;border:1px solid var(--line);border-radius:8px;background:var(--card);color:var(--ink);font:inherit}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:auto}
table{width:100%;border-collapse:collapse;min-width:720px}th,td{text-align:left;padding:12px 14px;border-bottom:1px solid var(--line);vertical-align:top}
th{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:var(--mute);font-weight:600}tr:last-child td{border-bottom:0}
code{font-size:12px;color:var(--mute)}.mute{color:var(--mute)}.num{text-align:right}.empty{text-align:center;color:var(--mute);padding:40px}
.pill{display:inline-block;padding:3px 10px;border-radius:99px;font-size:12px;font-weight:600}
.pill.pending{background:var(--warnbg);color:var(--warn)}.pill.sent{background:var(--okbg);color:var(--ok)}.pill.sending{background:var(--infobg);color:var(--info)}
.err{color:#c0392b;font-size:12px;margin-top:4px;max-width:260px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
</style></head><body><div class="wrap">
<h1>Back in stock requests</h1><p class="sub">{{rows.Count}} shown &middot; {{events}} webhook events received &middot; auto-refreshes every 15s</p>
<div class="stats"><div class="stat"><span>Waiting</span><b>{{C("pending") + C("sending")}}</b></div><div class="stat"><span>Notified</span><b>{{C("sent")}}</b></div><div class="stat"><span>Total</span><b>{{total}}</b></div></div>
<div class="bar"><div class="tabs">{{Tab("", "All", total)}}{{Tab("pending", "Pending", C("pending"))}}{{Tab("sending", "Sending", C("sending"))}}{{Tab("sent", "Notified", C("sent"))}}</div>
<form method="get"><input type="hidden" name="status" value="{{H(status)}}"><input name="q" value="{{H(search)}}" placeholder="Search email or SKU"></form></div>
<div class="card"><table><thead><tr><th>Email</th><th>Product</th><th>Status</th><th>Requested</th><th>Notified</th><th class=num>Tries</th></tr></thead><tbody>{{sb}}</tbody></table></div>
</div>
<script>document.querySelectorAll('time').forEach(t=>{const d=new Date(t.getAttribute('datetime'));if(!isNaN(d))t.textContent=d.toLocaleString([], {dateStyle:'medium',timeStyle:'short'})});setTimeout(()=>location.reload(),15000)</script>
</body></html>
""";
    }
}
