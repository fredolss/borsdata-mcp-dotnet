# Börsdata MCP – webbplats

Statisk svensk Astro-sida utan klient-JavaScript. Kräver Node.js 22.12+ och npm 9.6.5+.

```bash
cd site
npm install
npm run dev
```

Öppna `http://localhost:4321/borsdata-mcp-dotnet/`.

```bash
npm run build
npm run preview
```

Från repository-roten: `npm --prefix site run build` (installera beroenden först).
Byggresultatet finns i `site/dist/`. Preview använder samma base path som produktion.

## Innehåll

`src/pages/index.astro` innehåller startsidan, `src/components/` dess större sektioner,
`src/layouts/Layout.astro` metadata och `src/styles/global.css` utseendet.
`src/data/content.ts` innehåller den svenska verktygskatalogen. När verktygen ändras,
stäm av katalogen mot `src/BorsdataMcp/Tools`, root README och `mcpb/manifest.json`.
Exempeldata är påhittade. Webbplatsen gör inga API-anrop och tar inte emot API-nycklar.

## GitHub Pages

`.github/workflows/pages.yml` bygger vid relevanta pull requests och pushar till `main`.
Endast `main` deployas, även vid manuell körning. Bygget använder Astros officiella
GitHub Action med `path: ./site`. Befintlig .NET CI och releaser påverkas inte.

Aktivera **Settings → Pages → Build and deployment → Source → GitHub Actions**.
Om miljön `github-pages` kräver godkännande behöver deploymenten godkännas där.
Merge av feature-branchens PR till `main` startar den första publiceringen:
https://fredolss.github.io/borsdata-mcp-dotnet/

## Kontroll före leverans

Kontrollera det byggda resultatet med preview på rätt base path: länkar, favicon,
CSS, canonical och sitemap. Granska mobil, surfplatta och desktop, 200 % zoom,
tangentbordsnavigation och funktion utan JavaScript. Root README ändras enbart för
webbplatslänken; serverkod och befintliga workflows ska inte ändras.
