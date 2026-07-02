# PostPilot Nginx Production Config

These files document the Nginx configuration currently used on the Hetzner VPS.

## Files

- `postpilot-api.conf`
  - Main HTTPS reverse proxy config for `post-pilot.cloud-ip.cc`
  - Proxies traffic to the API container on `127.0.0.1:5122`
  - Handles HTTP -> HTTPS redirect
  - Blocks common bot/scanner paths like `.env`, `wp-login.php`, `phpunit`, `phpmyadmin`
  - Allows media uploads up to 100MB

- `postpilot-rate-limits.conf`
  - Defines per-IP Nginx rate limit zone
  - Current limit: `10r/s` with burst configured in `postpilot-api.conf`

- `postpilot-media.conf`
  - Reverse proxy for the public media hostname(s) to the MinIO container on `127.0.0.1:9000`
  - Currently serves both `media.publishharbor.com` (new) and `media.post-pilot.cloud-ip.cc`
    (legacy, kept alive temporarily during the domain migration) on the same server block
  - Only relevant when `MediaStorage__Provider=s3-compatible`; production defaults to
    `MediaStorage__Provider=Supabase`, which serves media directly from Supabase Storage
    and does not use this file

- `00-drop-unknown.conf`
  - Optional config to drop requests for unknown hosts / direct IP access

## Important

Do not commit SSL certificates or private keys.

Do not commit files from:

```text
/etc/letsencrypt/