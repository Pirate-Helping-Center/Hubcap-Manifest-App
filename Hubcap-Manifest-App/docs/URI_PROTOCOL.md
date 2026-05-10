# Hubcap Manifest App — URI Protocol Reference

The app registers a custom URI scheme (`hubcapapp://`) on install. Any browser link,
JS `window.location = "hubcapapp://..."`, or `<a href="hubcapapp://...">` will launch
(or foreground) the app and hand off the command to the in-process protocol handler.

## Scheme

```
hubcapapp://<action>[/<subaction>]/<appId>
```

- **scheme:** `hubcapapp` (lowercase, case-insensitive on parse)
- **appId:** numeric Steam AppID
- Path segments are `/`-separated
- Any unknown action is silently ignored (no crash, no error dialog)

## Actions

### 1. Download only

```
hubcapapp://download/<appId>
```

Downloads the manifest zip for `<appId>` from the configured manifest API
(`https://hubcapmanifest.com/api/v1/manifest/{appId}`) into the user's
configured downloads folder. Does **not** install.

**Example:**
```
hubcapapp://download/400
```

**User feedback:**
- Info toast: *"Starting download for App ID: 400"*
- Success toast: *"Download completed for App ID: 400"*
- Error toast if API key is missing or the download fails

---

### 2. Install from existing download

```
hubcapapp://install/<appId>
```

Installs a previously-downloaded `{appId}.zip` that is already sitting in the user's
downloads folder. Fails gracefully if the file isn't there.

**Example:**
```
hubcapapp://install/400
```

**User feedback:**
- Info toast: *"Starting installation for App ID: 400"*
- Success toast: *"Installation completed for App ID: 400. Restart Steam to see changes."*
- Error toast: *"File not found for App ID: 400. Please download it first."*

---

### 3. Download AND install (one-click)

```
hubcapapp://download/install/<appId>
```

The recommended action for a "Download" button on a site. Fetches the manifest zip,
installs it, and auto-deletes the zip on success. The whole flow runs in the app with
progress toasts.

**Example:**
```
hubcapapp://download/install/400
```

**User feedback:**
- Info toast: *"Starting download and install for App ID: 400"*
- Info toast: *"Download completed, now installing App ID: 400"*
- Success toast: *"Installation completed for App ID: 400. Restart Steam to see changes."*
- Info toast: *"Deleted ZIP file for App ID: 400"*
- Error toast on any failure

---

## Behavior notes for site implementation

- **App must be installed and registered.** The protocol is registered in
  `HKCU\Software\Classes\hubcapapp` on first launch of the app. Before that, clicking
  a `hubcapapp://` link does nothing (the OS will show a "no app associated" dialog).
  If you want to detect whether the app is installed, use a short navigation timeout
  fallback: start a timer after clicking the link, and if the page is still focused
  after ~1 second, assume the handler isn't installed and show an "Install the app"
  prompt.

- **API key is required.** All download actions require the user to have entered
  their API key in the app's Settings page. If it's missing, the action fails with
  an error toast and nothing happens on disk. You don't need to pass the key through
  the URI — the app reads it from local settings.

- **No query string, no fragment.** The handler only reads the path segments.
  Anything after `?` or `#` is discarded.

- **Case-insensitive action names.** `DOWNLOAD/INSTALL/400` works the same as
  `download/install/400`. AppID must be numeric.

- **Same-origin is irrelevant.** This is a registered URI scheme, not a web request —
  any page on any domain can trigger it. No CORS, no referrer checks.

- **The app runs single-instance.** If the app is already open, the URL is forwarded
  to the running instance rather than launching a second one. The running instance
  handles the action without stealing focus unless a notification opens it.

- **Progress and results surface as Windows toast notifications**, not modal
  dialogs. The user may miss them if notifications are disabled in Windows settings.

---

## HTML examples

### Plain download link
```html
<a href="hubcapapp://download/install/400">Download Half-Life 2</a>
```

### JavaScript trigger with install-detection fallback
```html
<button onclick="downloadGame(400)">Download</button>
<script>
function downloadGame(appId) {
  const before = Date.now();
  // Hidden iframe trick to avoid the "leaving this site" prompt in some browsers
  const frame = document.createElement('iframe');
  frame.style.display = 'none';
  frame.src = `hubcapapp://download/install/${appId}`;
  document.body.appendChild(frame);

  // If the page is still visible and focused after 1500ms, assume the handler
  // is not installed and prompt to install the app.
  setTimeout(() => {
    if (document.hasFocus() && Date.now() - before >= 1400) {
      if (confirm('Hubcap Manifest App not detected. Open the download page?')) {
        window.location = 'https://github.com/Hubcap-manifest/Hubcap-Manifest-App/releases/latest';
      }
    }
    frame.remove();
  }, 1500);
}
</script>
```

### Separate download and install buttons
```html
<a href="hubcapapp://download/400">Download only</a>
<a href="hubcapapp://install/400">Install (after download)</a>
<a href="hubcapapp://download/install/400">Download + Install</a>
```

---

## Summary table

| URL | Action | Needs API key | Needs existing zip |
|-----|--------|:---:|:---:|
| `hubcapapp://download/<appId>` | Download zip only | ✓ | — |
| `hubcapapp://install/<appId>` | Install existing zip | — | ✓ |
| `hubcapapp://download/install/<appId>` | Download + install + cleanup | ✓ | — |
