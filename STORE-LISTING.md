# Store listing copy

Paste-ready text for the Partner Center submission. Regenerate the version
number for each release; everything else is reusable.

Store policy notes before you submit:

- **No superlatives.** Microsoft's Store policies reject "best", "fastest",
  "#1" and similar claims. Everything below is factual.
- **No claims about other products.** Naming the browsers CleanMachine cleans
  caches for is factual and fine; implying affiliation or endorsement is not.
- **Description limit is 3,000 characters** including line breaks. The text
  below is under it.
- **Product features**: up to 20 short summaries, displayed as a bulleted list.
- **"What's new"**: leave **blank** for a first submission. The form says so
  explicitly, and there is no prior version to diff against.

---

## Description

```text
CleanMachine is a Windows cleanup utility that shows you what it would remove
before it removes anything. Nothing is deleted without a review step, and
nothing runs unattended that you have not explicitly turned on.

CLEAN WHAT ACTUALLY CLUTTERS YOUR PC
- Browser caches for Google Chrome, Microsoft Edge, Mozilla Firefox, Brave,
  Opera and Vivaldi, across every profile on the machine
- Temporary files, thumbnail and icon caches, error reports, internet cache,
  jump lists and other files Windows regenerates on demand
- Temp files left behind by installed desktop and Microsoft Store applications
- Old Windows Update download files, offered as an opt-in advanced category

REVIEW-FIRST REGISTRY CARE
Registry Care scans your per-user (HKCU) registry only, and never touches
machine-wide keys. It reports dangling file associations, leftover uninstall
entries, stale startup commands and cached program names. It will not delete
anything until it has written a .reg backup you can restore from, and a
confidence gate holds back low-risk findings unless you choose to see them.
Every key and value is shown before it is removed.

ONE CLICK, STILL REVIEW-FIRST
"Clean All Safe Items" runs every area's safe items behind a single
confirmation, with per-area figures, live progress and a cancel button.
Downloads, documents and advanced categories are never included in it.

AUTOMATIC CLEANING YOU OPT INTO
- Clean a browser's cache when it closes
- Clean when free disk space drops below a threshold you set
- Clean at sign-in, or after the PC has been idle for a set time
- Empty Recycle Bin items older than a set number of days
- Run on a daily, weekly, monthly or at-logon schedule, even while the app is
  closed

ALSO INCLUDED
Enable, disable and remove startup programs, and browse installed apps and
hand off uninstalls to each program's own uninstaller. An activity log records
every automated and manual run, and lifetime and 30-day cleanup totals are kept
per area. Any folder can be excluded from cleanup.

BUILT FOR REVIEW, NOT SURPRISE
Protected, recently modified, locked and reparse-point (junction) paths are
refused. The Windows component store is never modified. Registry changes can
always be restored from the backup CleanMachine takes first. The app runs
entirely in your own user account and never requests administrator rights.

NO ACCOUNT, NO ADS, NO TRACKING
No advertising, no telemetry, no third-party analytics, and no network
connections at all. Your settings, statistics, history and registry backups
stay on your PC.
```

## Product features

Enter these one per row (up to 20):

1. Review-first cleaning: every deletion is shown before it happens
2. Browser cache cleaning for Chrome, Edge, Firefox, Brave, Opera and Vivaldi, across all profiles
3. Temporary file, thumbnail, error report and internet cache cleanup
4. Temp file cleanup for installed desktop and Microsoft Store apps
5. Read-only per-user registry scanning, with a backup taken before any change
6. One-click restore from the registry backup CleanMachine created
7. "Clean All Safe Items" behind a single confirmation, with progress and cancel
8. Automatic cleaning when a browser closes, when disk space runs low, at sign-in or when idle
9. Recycle Bin auto-empty for items older than a chosen age
10. Daily, weekly, monthly and at-logon schedules that run while the app is closed
11. Startup program management: enable, disable and remove auto-start entries
12. Installed app browser that hands off to each vendor's own uninstaller
13. Activity log of every automated and manual cleanup run
14. Lifetime and last-30-days cleanup statistics
15. Exclude any folder from cleanup
16. System tray support: start minimised, minimise or close to tray
17. No advertising, no telemetry, no third-party analytics
18. Runs in your own user account and never requests administrator rights

## Store logo (1:1 app tile icon)

**Optional for apps, and recommended.** The Partner Center form offers three
logo slots. For an app only one applies:

| Slot | Size | Applies to you? |
|---|---|---|
| 1:1 app tile icon | 300 x 300 | **Yes - use this** |
| 2:3 poster art | 720 x 1080 | Games only |
| 1:1 box art | 1080 x 1080 | Games only |

If you leave the 300 x 300 slot empty the Store falls back to the icon inside
your package, which already validated - so nothing is blocked. Uploading it is
worth doing anyway, because a supplied icon **takes priority over** the one in
the package and renders crisper on high-DPI Store surfaces.

[`store/Square300x300AppTileIcon.png`](store/Square300x300AppTileIcon.png) is
ready to upload: 300 x 300 PNG, 21 KB, rendered from the same
`Assets/generate_icons.py` "Fresh Screen" mark as the in-package icons, so the
Store icon and the app icon are the same artwork.

It lives in `store/` rather than `Assets/` on purpose. The project only globs
`Assets/**` into the MSIX package, so a Store-only asset cannot accidentally
bloat the submission package.

To regenerate it after an icon change:

```powershell
pip install Pillow
python -c "import sys; sys.path.insert(0, 'CleanMachine.Windows/Assets'); import generate_icons as g; g.render_mark(300).save('store/Square300x300AppTileIcon.png')"
```

## Screenshots

Requirements: **Desktop minimum 1366 x 768**, up to 3840 x 2160 (4K). PNG only,
up to 10 desktop screenshots, 50 MB each. One is required; four or more is
recommended. Ignore the **Xbox** tab - this app is not published to Xbox.

Two guidelines that are easy to get wrong: keep important text and UI in the
**top two-thirds** of the image, because the Store overlays text on the bottom
third; and do not bake your own logos, captions or marketing text into the
screenshot. Each one also takes an optional caption of 200 characters or less,
and the order you upload is the order they display - drag to reorder after
uploading.

Capture at 1920 x 1080 or larger, in this order (first one is the hero shot):

1. **Overview** - the dashboard with the four availability cards and the
   lifetime/30-day figures. Best first impression: it shows the whole product
   at once and reinforces the "review-first" idea.
2. **Browser Cleaner** - two-pane list with the size/file/item chips visible.
3. **Windows Cleanup** - the category list, so the breadth is obvious.
4. **Registry Care** - shows the backup/restore confidence story.

Tips that matter for certification:

- No personal data in any shot. Run screenshots against a fresh profile, or
  blur browser profile names, file paths and user names.
- Show real, populated lists. An empty-state screenshot reads as a broken app.
- No certificate warnings, SmartScreen prompts, or debug windows in frame.

## Privacy policy URL

Required. Publish [`PRIVACY.md`](PRIVACY.md) and point the Properties field at
the hosted copy. The text there is written to be published as-is, and every
claim in it is verifiable against the source: the app has no network code and
no analytics packages.

Cheapest options, no hosting account needed: GitHub Pages, or a GitHub Gist
made public. Either gives you a stable URL you own.

## Other required fields

- **Category**: Utilities
- **Contact details**: required for business/company accounts
- **Age ratings**: complete the whole questionnaire. Every answer is "no" for a
  local file-cleanup utility.
- **Submission options -> Restricted capabilities**: required, because the
  package declares `runFullTrust`. Paste the justification from
  [`RELEASE.md`](RELEASE.md).
