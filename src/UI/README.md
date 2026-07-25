Minimal UI for submitting invoices to the `GatewayService`.

How to use:

1. Open `src/UI/index.html` in a browser (double-click it, or run the full stack with `docker compose up` — it's already served by nginx, see `Dockerfile`/`nginx.conf`).
2. Adjust the "Gateway URL" field if your `GatewayService` runs on a different port (e.g. `http://localhost:5000`).
3. Attach a receipt photo and submit — Vendor/Amount/LineItems are read from the photo itself, not typed (dev branch only, see `docs/adr/008-receipt-photo-submission.md`).

Notes:
- This UI is static, with no build step — `app.js`/`index.html` are plain JS/HTML.
- Sample invoice fixtures are in `docs/sample-invoices.json`.
