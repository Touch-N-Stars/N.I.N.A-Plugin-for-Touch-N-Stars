# N.I.N.A Plugin for Touch-N-Stars
This plugin integrates [Touch'N'Stars](https://github.com/Touch-N-Stars/Touch-N-Stars) with the astrophotography software **NINA** (Nighttime Imaging 'N' Astronomy).

### 🚀 **Current Status: Beta Version**  
The plugin is currently in development and may contain bugs.

### 🔧 **Installation**
To use the plugin, copy the contents of the ZIP file to NINA's plugin directory.
This is usually located at: %LOCALAPPDATA%/NINA/Plugins/3.0.0/Touch-N-Stars
If the folder doesn't exist, please create it first.

The release package includes the offline `celestia-atlas-data` tree. Landscapes
created by Touch'N'Stars are stored outside the replaceable plugin directory,
normally below
`%LOCALAPPDATA%\NINA\Touch-N-Stars\celestia-atlas-data\landscapes` on Windows.
Existing generated landscapes are migrated automatically.

### 🧩 **Important Notes** 
- The **Advanced API** plugin is required in the latest version.
  The API port must be set to 1888 and V2 must be enabled.
  Additionally, "Use Access-Control-Allow-Origin Header" must be enabled.
- Version 2.2.2.0 or newer is required for Three Point Polar Alignment.
