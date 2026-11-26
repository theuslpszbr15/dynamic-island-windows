// Material Symbols Rounded (Apache 2.0, google/material-design-icons) as Android vector drawables:
// res/drawable/ms_<name>.xml (outlined) and ms_<name>_fill.xml (filled, for the selected tab).
// node android/tools/material-icons.mjs
import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const names = ["smart_toy", "auto_awesome", "sports_esports", "folder", "computer", "mouse", "movie", "slideshow", "mic", "power_settings_new",
  "music_note", "send", "history"];
const base = "https://raw.githubusercontent.com/google/material-design-icons/master/symbols/web";
const out = join(dirname(dirname(fileURLToPath(import.meta.url))), "app", "src", "main", "res", "drawable");

for (const name of names) {
  for (const [variant, suffix] of [["", ""], ["_fill1", "_fill"]]) {
    const svg = await (await fetch(`${base}/${name}/materialsymbolsrounded/${name}${variant}_24px.svg`)).text();
    const paths = [...svg.matchAll(/<path d="([^"]+)"/g)].map((m) => m[1]);
    if (paths.length === 0) throw new Error(`${name}${variant}: no path`);
    // most symbols draw on "0 -960 960 960"; a few older ones have no viewBox and draw on 24 x 24
    const [minX, minY, w, h] = (svg.match(/viewBox="([^"]+)"/)?.[1] ?? "0 0 24 24").split(/\s+/).map(Number);
    writeFileSync(join(out, `ms_${name}${suffix}.xml`), `<?xml version="1.0" encoding="utf-8"?>
<!-- Material Symbols Rounded "${name}"${suffix ? " (filled)" : ""}, Apache 2.0 (google/material-design-icons) -->
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp" android:height="24dp" android:viewportWidth="${w}" android:viewportHeight="${h}">
    <group android:translateX="${-minX}" android:translateY="${-minY}">
${paths.map((d) => `        <path android:fillColor="#FFFFFFFF" android:pathData="${d}" />`).join("\n")}
    </group>
</vector>
`);
  }
}
console.log(`${names.length * 2} icons`);
