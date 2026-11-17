# Customized Windows source snapshot

This repository preserves a customized source snapshot, not a new upstream release or a tested ready-to-run application installer.

- The original GPL-3.0 license and upstream credits are retained.
- Application source files are copied without behavioral changes. Prebuilt Windows and Linux native libraries are not included in this public source publication.
- The previously used Windows native binary differs from the official VIIPER v1.0.0 Windows release artifact checked during preparation. Its exact corresponding source has not been established, so that binary remains outside this public repository. See NATIVE_DEPENDENCY.md for source/license references and preparation limitations.
- Native libraries must be acquired separately and checked for compatibility with the bindings in this snapshot. No fresh Windows or Linux build is claimed.
- Personal settings, controller identities, logs, recordings, and local diagnostic evidence are excluded. Device identifiers visible in five documentation illustrations are masked; application source is unchanged.
- Automatic GitHub workflows are intentionally excluded. No Pages deployment, release publication, or collaborator sharing is configured by this upload.
- Prior automated checks are historical. Native audio, microphone quality, and intermittent per-game input issues are not represented as universally fixed or verified.

The executable currently used on the original PC is not distributed here as an installer. Building the source or supplying a portable binary release is a separate step.
