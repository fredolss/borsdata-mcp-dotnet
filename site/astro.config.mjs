import { defineConfig } from "astro/config";
import sitemap from "@astrojs/sitemap";

export default defineConfig({
  site: "https://fredolss.github.io",
  base: "/borsdata-mcp-dotnet",
  output: "static",
  trailingSlash: "always",
  integrations: [sitemap()],
});
