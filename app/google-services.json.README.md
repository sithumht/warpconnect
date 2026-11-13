# Firebase — required for public release

Crashlytics is wired up in the app, but the build **cannot succeed** until you
drop a Firebase `google-services.json` into `app/`.

## One-time setup (~3 minutes)

1. Open https://console.firebase.google.com/ and click **Create a project** (or use an existing Google Cloud project — no need to enable billing; Spark plan is enough).
2. Give it a name (e.g., `warpconnect-stats`). Google Analytics is optional — disable it since the app tracks nothing itself.
3. Click **Add app** → **Android**.
4. Register the app:
   - Android package name: **`com.warpconnect.app`**
   - App nickname (any): `warpconnect`
   - SHA-1 certificate — skip for now (not needed for Crashlytics).
5. Click **Register app** → **Download `google-services.json`**.
6. Save the file to **`app/google-services.json`** next to this file.
7. (Optional but recommended for releases) Go back to **Project settings → Your apps** and confirm the SHA-1/SHA-256 once you generate your release keystore.

## Verifying

```
.\gradlew.bat :app:assembleDebug
```

If `google-services.json` is present and matches `applicationId = "com.warpconnect.app"`,
the build will succeed.

Crashlytics works via the debug and release configurations; nothing else to wire up —
crash data accumulates automatically.

## What you're seeing locally

If you see:

```
File google-services.json is missing.
The Google Services Plugin cannot function without it.
```

...you have not done steps 5–6 above, or the file doesn't match `applicationId`.

The file is excluded from git (see `.gitignore`). Do not commit it.
