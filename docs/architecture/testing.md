# Test pyramid


## Test pyramid
Unit (domain, rules, playback, caps) > architecture (dependency rules, every endpoint has policy) > integration (real emulators) > component (web) > E2E (Playwright on the cluster, axe) > smoke > chaos and load.
Every case at every level, with its status: `docs/test-matrix.md`.
Every requirement ID appears in a test name: `REQ_BT_001` in .NET, `REQ-BT-001` in E2E and scripts. `tests/check-traceability.ps1` fails on an ID with no test.
