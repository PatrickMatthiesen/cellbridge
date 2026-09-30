# CellBridge demo

This is a standalone Razor Pages catalog for the collaboration server. It calls
`GET /api/documents` from the server process and renders a small searchable file
library. Supported Word, Excel, and PowerPoint files get an Office `ofe|u|` launch
link; every file gets a download link when its path is valid.

Use **New document** to choose Word, Excel or PowerPoint and enter a filename.
The file appears in the library after creation. Names cannot overwrite existing
files. Created files and edits remain in memory until the server restarts.

Run it from this directory with:

```powershell
dotnet run --launch-profile https
```

Set `CollabServer:BaseUrl` to the server address used by the demo process. Set
`CollabServer:PublicBaseUrl` when the server has a different address visible to
the browser or desktop Office client. Aspire can inject the base URL with
`CollabServer__BaseUrl` and the public URL with `CollabServer__PublicBaseUrl`.

The Office links follow Microsoft's [Office URI schemes](https://learn.microsoft.com/en-us/office/client-developer/office-uri-schemes).
