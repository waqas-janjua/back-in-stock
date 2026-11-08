# Back-in-Stock Alerts (ASP.NET Core 8 + SQLite)

## Env vars
`STOCK_API_URL`, `STOCK_API_KEY`, `RESEND_API_KEY`, `RESEND_BASE_URL`, `WEBHOOK_SECRET`, `ADMIN_PASSWORD` (required).
Optional: `PORT` (default 8080 in Docker), `DB_PATH` (default `/data/backinstock.db` in Docker), `ENV_FILE` (default `.env.local`, loaded if present).

## Run
    docker build -t back-in-stock .
    docker run -p 8080:8080 -v bis-data:/data --env-file .env.local back-in-stock

Local: `cd BackInStock && dotnet run` (reads `.env.local`).

Endpoints: `GET /embed`, `POST /api/requests`, `POST /webhooks/stock`, `GET /admin` (HTTP Basic, any username, password = `ADMIN_PASSWORD`).

## No duplicate or missed emails
- One open request per (email, SKU): a partial unique index in SQLite; repeat sign-ups are idempotent.
- The webhook is authenticated (constant-time secret check) and its quantity is ignored; live stock comes from the stock API.
- Each request is claimed atomically (`pending -> sending` via a conditional UPDATE), so concurrent or repeated webhooks cannot both send. It becomes `sent` only after the email API accepts it.
- Email failure releases the claim (`pending`) and the webhook returns 503 so the sender retries.
- A background sweeper (every 15s) re-checks stock for pending requests, which covers missed webhooks and failed emails. Claims stuck in `sending` for over 2 minutes (crash mid-send) are released.
- Each email carries a stable `Idempotency-Key` (`bis-<request id>`) as defence in depth.
- State is in SQLite (WAL), so it survives restarts; mount `/data`.
