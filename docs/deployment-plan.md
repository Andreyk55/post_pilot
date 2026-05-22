# VPS Deployment Plan

> **Superseded.** The original plan in this file described a 5-container stack
> (api + publisher + postgres + minio + containerised nginx) under
> `deploy/docker-compose.server.*.yml`. That layout has been replaced.
>
> Production deployment now lives under [prod/](../prod/) and uses host
> nginx + 3 containers (api + worker + postgres). The full runbook is at
> **[docs/deployment-vps.md](deployment-vps.md)**.
