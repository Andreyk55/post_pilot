# Frontend deployment (GitHub Actions → Vercel)

The frontend is a React + Vite SPA. **GitHub Actions builds it** and deploys
the prebuilt output to Vercel; Vercel is used only as a static host + CDN.

The workflow lives at
[`.github/workflows/deploy-frontend.yml`](../.github/workflows/deploy-frontend.yml).

## Trigger

- Push to `develop` that touches `frontend/**`, `vercel.json`, or the workflow
  itself.
- Manual run via the Actions tab (`workflow_dispatch`).

## Pipeline

1. Checkout
2. Setup Node.js LTS with npm cache keyed on `frontend/package-lock.json`
3. `npm ci` inside `frontend/`
4. `npm run lint` *(non-blocking — failures appear in the log but do not stop the deploy)*
5. `npm run build` (runs `tsc -b && vite build` — typecheck + build, **blocking**)
6. Install the Vercel CLI globally
7. `vercel pull` — fetch the project's production env vars + settings
8. `vercel build --prod` — produce the `.vercel/output` directory the CLI expects
9. `vercel deploy --prebuilt --prod` — upload the prebuilt artifact

## Required GitHub secrets

Add these under **Settings → Secrets and variables → Actions → Secrets**:

| Secret              | Where to find it                                                                                       |
| ------------------- | ------------------------------------------------------------------------------------------------------ |
| `VERCEL_TOKEN`      | https://vercel.com/account/tokens — create a token scoped to the org.                                  |
| `VERCEL_ORG_ID`     | Run `vercel link` locally inside `frontend/`, then read `.vercel/project.json` (field: `orgId`).        |
| `VERCEL_PROJECT_ID` | Same `.vercel/project.json` (field: `projectId`).                                                       |

## Required GitHub variable (optional)

| Variable        | Example value              | Notes                                                              |
| --------------- | -------------------------- | ------------------------------------------------------------------ |
| `VITE_API_URL`  | `https://api.<domain>/api` | Used by the workflow's standalone `npm run build` step (sanity check). |

The **authoritative source** for `VITE_API_URL` is the Vercel project's env
config (see below) — that's what `vercel build` reads after `vercel pull`.
The GH Actions variable only feeds the early build step; you can omit it and
rely solely on Vercel's env if you prefer one source of truth.

## Vercel project setup (one-time)

1. Create the Vercel project (import from Git or `vercel link` locally inside
   `frontend/`).
2. Under **Settings → Environment Variables** set, for the Production
   environment:

   | Name           | Value                       |
   | -------------- | --------------------------- |
   | `VITE_API_URL` | `https://api.<domain>/api`  |

3. **Disable Vercel's Git auto-deploy** to prevent double deployments
   (see next section).

The repo includes [`frontend/vercel.json`](../frontend/vercel.json), which
contains only the SPA rewrite rule so react-router deep links resolve to
`index.html`. `vercel build` (run from `frontend/` by the workflow) picks
this up automatically and bakes it into the prebuilt output.

## Disabling Vercel's Git auto-deploy

If both GitHub Actions **and** Vercel's Git integration are active, every
push will trigger two builds. Disable Vercel's side:

- **Option A — Disconnect Git entirely** (recommended for this setup):
  Vercel project → **Settings → Git → Disconnect**. Deploys then happen
  exclusively via the CLI from GitHub Actions.

- **Option B — Keep Git connected but ignore all pushes**:
  Vercel project → **Settings → Git → Ignored Build Step** → set the command
  to `exit 0` (or `echo "skip" && exit 0`). Vercel will receive webhooks but
  skip every build. Useful if you still want PR comments / preview URLs
  driven by other Vercel integrations.

If Vercel was never connected to the repo's Git provider, no action is
needed — GitHub Actions is the only path.

## CORS / backend requirements

The backend at `https://api.<domain>` must allow the Vercel deployment origin
(`https://<project>.vercel.app` and any custom domain) in its CORS config.
Backend deployment is out of scope for this document.

## Local build verification

```bash
cd frontend
npm ci
npm run lint
npm run build
```

`npm run build` runs `tsc -b && vite build` and writes to `frontend/dist/`.
