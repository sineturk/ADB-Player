# Security Policy

## Supported version

The public security support target for the initial release is ADB Player 1.0.x on Windows x64.

## Reporting a vulnerability

Please do not publish credentials, account data, private URLs, exploit details or security-sensitive logs in a public issue.

Use GitHub's **Report a vulnerability / Private vulnerability reporting** feature when it is enabled for the public repository. If that feature is unavailable, contact the repository owner through the private contact method listed on the owner's GitHub profile and provide only the minimum information needed to establish a private channel.

General bugs that do not expose security-sensitive information may be reported through GitHub Issues.

## Secrets

Real OAuth client secrets, Telegram API credentials, AltyazıDB API keys, ADB Cloud database credentials, encryption keys, signing certificates and signing passwords must never be committed.

ADB Player build tooling reads developer credentials from ignored local environment files or environment variables and generates local C# credential files. Public source snapshots must contain only secret-free fallbacks.

## Release verification

Official releases should provide SHA-256 hashes for the installer and portable archive. When code signing is used, signatures should be timestamped and verified before publication.

Download release binaries only from the official GitHub Releases page or another location explicitly linked by the project maintainers.

## Security boundaries

ADB Player treats remote URLs, add-ons, indexers, archives, torrent metadata and external media as untrusted input. Features that can expose local-network services or execute media/tooling paths should keep explicit validation, HTTPS/local-network policy checks and safe-apply gates enabled.
