# SlinnerB Studio Relay Server

Tiny HTTP relay that hosts collaborative sessions for SlinnerB's Music Studio.

## Run it

```
SlinnerBStudioServer.exe
```

Optional flags:

- `--port 8090`  listen on a different port (default 8090)
- `--data C:\path\to\sessions`  where to store session data (default: `data` next to the exe)

## First-time setup on Windows Server

1. **Open the firewall** for the port:

   ```
   netsh advfirewall firewall add rule name="SlinnerBStudio Relay" dir=in action=allow protocol=TCP localport=8090
   ```

2. **Allow non-admin to bind the port** (otherwise you'll see "Access is denied" unless you run as Administrator):

   ```
   netsh http add urlacl url=http://+:8090/ user=Everyone
   ```

3. Make sure port 8090 is open at the network edge (router / cloud security group / etc).

4. Start the exe. You should see:

   ```
   SlinnerB Studio relay listening on port 8090, data in C:\...\data
   0 session(s) loaded.
   ```

5. Verify from another machine: `http://your-server-public-address:8090/health` should return `ok`.

## Tell your friends the URL

In the app:  **Work with Friends → Session Settings → Server URL = `http://your-server-public-address:8090`**

## Run as a Windows Service (optional)

Easiest: use NSSM (https://nssm.cc/).

```
nssm install SlinnerBStudioRelay  "C:\path\to\SlinnerBStudioServer.exe"
nssm start SlinnerBStudioRelay
```

## Notes

- Sessions persist to disk (`data\<CODE>.blob` + `<CODE>.json`). Safe to restart the server.
- 200 MB per-session size cap.
- No authentication beyond the 8-character session code (~40 bits of entropy). Treat codes as semi-private — anyone with the code can read or overwrite the session.
- For HTTPS, run behind a reverse proxy (IIS, Caddy, nginx).
