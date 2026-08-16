## What and why

<!-- What does this change do, and why? Link any related issue, e.g. Fixes #123 -->

## Checklist

- [ ] Targets the `develop` branch (not `master`)
- [ ] `dotnet build` and `dotnet test` pass locally
- [ ] Client checks pass (`npm run check:i18n`, `npx tsc -b`, `npx vitest run`) if the client changed
- [ ] User-facing text is provided in **both Spanish and English**
- [ ] Docs updated if behaviour or the API changed (`docs/`, in-product Help, `docs/openapi/v1.json`)
- [ ] No secrets, credentials, or encrypted-folder contents committed
