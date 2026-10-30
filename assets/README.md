# Brand assets

The bracket-and-USB identity comes from the user-supplied DongleDeck design kit. The SVG masters preserve that geometry; the Windows glyphs use the kit's simplified shapes for small sizes.

| Folder | Contents |
| --- | --- |
| `branding/` | Original dark/light SVG masters and high-resolution PNGs |
| `github/` | Dark/light README banners and a 1280×640 social preview |
| `windows/` | Multi-resolution `DongleDeck.ico`, individual glyphs, and light/dark app-header images |

Primary colors: cyan `#00F0FF`, blue `#0076CE`, dark canvas `#070C18`, light canvas `#F8FAFC`.

The app uses the built-in Segoe UI font, so no font installation is required. Artwork has local font fallbacks. All app images are embedded in the executable. The editable SVGs are included for future design changes.

No live system-tray state assets are implemented. Banner text describes the actual v0.4.0 feature set; the design kit's example CLI, package-manager command, repository path, and star counts are not product claims.
