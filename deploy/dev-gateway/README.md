# Development VedAstro gateway

The fork is missing core calculator source and cannot build its .NET API. This development deployment forwards `/api/Calculate/*` to the hosted VedAstro API, preserving request bodies, headers and upstream failures. It does not run a local calculator or certify calculation accuracy.

Coolify settings: branch `dev`, build context `/`, Dockerfile `/deploy/dev-gateway/Dockerfile`, container port `80`, health path `/health`. No database or OpenRouter key is required. `/health` checks the gateway process; test an actual calculation separately to check the upstream service.

Hosted API limits and unavailable calculations still apply. Keep production calculation verification enabled in AstroFriend.

The gateway bundles the hosted API's YR1 intermediate, cross-signed Root YR and standard ISRG Root X1 certificates. The chain was validated by the operating system against ISRG Root X1 before export. TLS verification stays enabled with depth four; no leaf certificate is trusted directly.
