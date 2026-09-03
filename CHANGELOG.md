# Touch 'N' Stars

## 1.4.0.0
- feat(filesystem): add server-side preview/imageinfo endpoints
- fix(filesystem-preview): read the Bayer pattern from the file header, so OSC FITS/XISF frames offer the debayer option and render with their actual pattern

## 1.3.2.0
- Update Touch N Stars WebApp

## 1.3.1.0
- Update Touch N Stars WebApp

## 1.3.0.0
- Generated Celestia Atlas landscapes are now stored in the persistent N.I.N.A. user data; existing app-local landscapes are migrated without overwriting user content
- The `celestia-atlas-data` directory is now packaged and served

## 1.2.7.6

- Fixed PHD2 RPC responses being matched by id, to survive an event flood while guiding

## 1.2.7.5

- Fixed a missing DLL in the plugin package

## 1.2.7.4

- Added a Stellarium-Web landscape generator with automatic installation of new landscapes
- Fixed landscape generator path parsing

## 1.2.7.3

- Added an adjustable image stretch slider to the PHD2 settings

## 1.2.7.1

- Added filesystem/filebrowser endpoints and a FITS analysis / plate solve endpoint
- Added a generic proxy endpoint (`GET /api/proxy`)
- Added an endpoint to read file content as text
- Extended the plugin passthrough endpoints (plugin9-plugin59)
- Fixed JSON serialization for NINA 3.2.0 compatibility

## 1.2.7.0

- Added LX200 GPS mount support

## 1.2.6.0

- Added INDI driver list endpoints

## 1.2.5.0

- Various PHD2 fixes

## 1.2.4.0

- Added PHD2 shared-memory (SHM) communication and profile management (create/rename/select) endpoints
- Added reboot/shutdown support for PINS boxes and Linux

## 1.2.3.2

- Added Linux support

## 1.2.3.0

- Added a Metrics endpoint
- Added Bahtinov mask focuser support

## 1.2.2.0

- Filtered AvalonDock panes out of dialog handling

## 1.2.1.0

- Added mDNS discovery for automatic server detection
- Instance name now gets a suffix when the port changes

## 1.2.0.0

- Reworked dialog handling and the slew & center flow

## 1.1.6.0

- Added a Framing Assistant controller and Target Search endpoints
- Standardized dialog handling (confirm/cancel/timeout) across mount actions
- Added slew & center with cancel support
- Added a Meridian Flip dialog with step tracking

## 1.1.2.0 - 1.1.3.0

- Added filter wheel and rotator endpoints
- Added a Telescopius proxy integration
- Fixed PHD2 connection stability issues

## 1.1.1.0

- Added an endpoint to save settings
- Added autofocus-run detection

## 1.1.0.0

- Added comprehensive PHD2 guiding integration (REST API, star lost detection, parameter control, equipment status)
- Reworked image loading (no longer mirror-inverted); added guide star image/stats endpoints

## 1.0.5.0 - 1.0.9.0

- Added Favorites API endpoints (including rotation info, persisted to favorites.json)
- Reworked CORS handling (EmbedIO-based, preflight support)
- Added automatic port discovery, advanced-API-port and version endpoints
- Added Stellarium integration endpoints; fixed app reload routing (404 on refresh)
- Added shutdown/restart of the host PC (incl. forced shutdown, and while PHD2 is running)
- Added flats support, autofocus-finished indicator and AF watcher

## 1.0.0.2 - 1.0.4.4

- Initial REST API: proxy, network connection details, app endpoints, guiding start/stop, switch control
- Made CORS configurable
- Added Android app info

## 1.0.0.1

- Initial release
