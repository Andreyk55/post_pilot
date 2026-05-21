# Frontend deployment (Vercel)

The frontend is a React + Vite SPA. **Vercel builds and deploys it** directly
from the Git repository — no GitHub Actions are involved.

## How it works

The Vercel project (`post-auto-pilot`) is connected to the GitHub repo via
Vercel's native Git integration. On every push to a tracked branch, Vercel:

1. Pulls the latest commit.
2. Runs the install + build commands inside the project's **Root Directory**.
3. Serves the resulting `dist/` folder from its global CDN.

## Vercel project settings

Under **Settings → Build and Deployment**:

| Setting             | Value          |
| ------------------- | -------------- |
| Framework Preset    | Other          |
| Root Directory      | `frontend`     |
| Install Command     | `npm install`  |
| Build Command       | `npm run build`|
| Output Directory    | `dist`         |
| Node.js Version     | 22.x (LTS)     |

> **Note on Node version:** the project is currently set to `24.x`. 22.x or
> 20.x (current Vercel-supported LTS lines) are safer choices. Adjust under
> **Settings → Build and Deployment → Node.js Version** if needed.

`npm run build` runs `tsc -b && vite build` (typecheck + bundle), so a real
type error will fail the deploy.

## SPA routing

The repo includes [`frontend/vercel.json`](../frontend/vercel.json) with a
single rewrite rule that sends every unknown path to `/index.html`. This is
what makes react-router deep links work on refresh / direct navigation.
Without it, e.g. `https://<domain>/posts/123` would return a 404.

## Environment variables

Set under **Settings → Environment Variables** for the **Production**
environment (and Preview if you want preview deploys to hit a real API):

| Name           | Value                       | Notes                                       |
| -------------- | --------------------------- | ------------------------------------------- |
| `VITE_API_URL` | `https://api.<domain>/api`  | Base URL of the backend, including `/api`.  |

`VITE_API_URL` is consumed at build time by
[`frontend/src/config/appConfig.ts`](../frontend/src/config/appConfig.ts).
Changes require a redeploy because Vite inlines `import.meta.env.*` into the
bundle.

See [`frontend/.env.example`](../frontend/.env.example) for the
local-development template.

## CORS / backend requirements

The backend at `https://api.<domain>` must allow the Vercel deployment origin
(`https://<project>.vercel.app` and any custom domain) in its CORS config.
Backend deployment is out of scope for this document.

## Local build verification

```bash
cd frontend
npm install
npm run build
```

The build writes to `frontend/dist/`. Vercel runs the equivalent commands on
every push.

## Triggering a deploy manually

- **Push to the tracked branch** (e.g. `develop` or `main` — whichever is
  configured as the Production branch in **Settings → Git**).
- Or in the Vercel dashboard: **Deployments → Redeploy** on any prior
  deployment.
