# Security

## Reporting a problem

Please don't open a public issue for a security problem. Use GitHub's private
[**Report a vulnerability**](https://github.com/Namanjeet13/Crest/security/advisories/new) form instead.
Include what you found, how to reproduce it and which version you're on. You'll get a reply as soon as possible.

## Supported versions

Only the [latest release](https://github.com/Namanjeet13/Crest/releases/latest) gets fixes.

## What Crest can touch

- **Files:** its own folder (`%LocalAppData%\Programs\Crest`) and its settings (`%AppData%\Crest`). Clipboard history is encrypted with Windows DPAPI for your account only.
- **Registry:** only the current user's sign-in entry (`Run`) and the Apps list entry (`Uninstall\Crest`).
- **Network:** none. Crest makes no network requests.
- **Other apps:** it reads window titles and presses buttons through Windows UI Automation, for media and Discord controls. It never reads typed text or passwords.
