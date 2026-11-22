# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/),
certificate by [SignPath Foundation](https://signpath.org/).

## Scope

Only the Windows binary (`BorsdataMcp.exe`, shipped inside the
`borsdata-mcp-<version>-win-x64.mcpb` release asset) is signed. It is built
from this repository's source by the `release-windows` job in
[`.github/workflows/build.yml`](.github/workflows/build.yml) on GitHub-hosted
runners, and only for tagged releases (`v*`). Nothing built outside that
workflow is ever submitted for signing.

## Team roles

| Role      | Members                                      |
|-----------|----------------------------------------------|
| Authors   | [fredolss](https://github.com/fredolss)      |
| Reviewers | [fredolss](https://github.com/fredolss)      |
| Approvers | [fredolss](https://github.com/fredolss)      |

- **Authors** commit to this repository.
- **Reviewers** review all changes from contributors who are not authors
  before they are merged.
- **Approvers** manually approve every signing request in SignPath before a
  release is signed.

All team members use multi-factor authentication on GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or
operating it.

Specifically, the program only sends requests to the
[Börsdata API](https://apidoc.borsdata.se/) (`apiservice.borsdata.se`), using
the API key that the user supplies from their own Börsdata subscription. Those
requests contain only the parameters needed for the data the user (or the MCP
client acting on the user's behalf) asked for, plus that API key. No
telemetry, analytics, crash reports or any other data are sent anywhere else.
